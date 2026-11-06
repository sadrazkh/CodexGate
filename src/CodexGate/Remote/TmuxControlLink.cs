using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace CodexGate.Remote;

/// <summary>
/// One web terminal attached to one tmux session through tmux control mode ("tmux -C"), which talks
/// plain lines over stdin/stdout: commands go in, "%begin…%end" replies and "%output" lines come out.
/// No pseudo-terminal is needed on the gateway's side.
/// </summary>
public sealed class TmuxControlLink : ITerminalLink
{
    private enum Mode
    {
        Discard, // before the screen is captured: the capture already contains it
        Hold,    // captured but not yet sent: keep, send after the capture
        Live,
    }

    private sealed record Pending(TaskCompletionSource<List<string>> Reply, bool HoldAfter);

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<Pending> _pending = new();
    private readonly List<byte[]> _held = [];
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ILogger _log;
    private Mode _mode = Mode.Discard;
    private bool _firstBlockSeen;
    private string _pane = "";
    private int _disposed;

    private TmuxControlLink(Process process, ILogger log)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        _log = log;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(DrainErrorsAsync);
    }

    public ChannelReader<byte[]> Output => _output.Reader;

    /// <summary>Starts the control client, then draws the current screen before live output.</summary>
    public static async Task<TmuxControlLink> AttachAsync(
        ProcessStartInfo start, string session, int cols, int rows, ILogger log, CancellationToken ct)
    {
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        var process = Process.Start(start) ?? throw new RemoteHostException("Could not start the session helper.");
        var link = new TmuxControlLink(process, log);
        try
        {
            var pane = (await link.CommandAsync($"display-message -p -t {session} '#{{pane_id}}'", ct)).FirstOrDefault()?.Trim();
            if (pane is null || !pane.StartsWith('%'))
            {
                throw new RemoteHostException("The session has no pane.");
            }
            link._pane = pane;
            await link.CommandAsync($"refresh-client -C {cols}x{rows}", ct);
            var screen = await link.CommandAsync($"capture-pane -p -e -t {pane}", ct, holdAfter: true);
            var cursor = (await link.CommandAsync($"display-message -p -t {pane} '#{{cursor_x}} #{{cursor_y}}'", ct)).FirstOrDefault() ?? "0 0";
            var parts = cursor.Split(' ');
            var x = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cx) ? cx : 0;
            var y = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cy) ? cy : 0;
            lock (link._lock)
            {
                link._output.Writer.TryWrite(TmuxProtocol.InitialScreen(screen, x, y));
                foreach (var chunk in link._held)
                {
                    link._output.Writer.TryWrite(chunk);
                }
                link._held.Clear();
                link._mode = Mode.Live;
            }
            return link;
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        foreach (var command in TmuxProtocol.SendKeys(_pane, data.Span))
        {
            _ = CommandAsync(command, ct); // replies arrive in order; nothing to wait for
        }
        await Task.CompletedTask;
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct) => CommandAsync($"refresh-client -C {cols}x{rows}", ct);

    private async Task<List<string>> CommandAsync(string command, CancellationToken ct, bool holdAfter = false)
    {
        var pending = new Pending(new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously), holdAfter);
        await _write.WaitAsync(ct);
        try
        {
            lock (_lock)
            {
                _pending.Enqueue(pending);
            }
            await _stdin.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), ct);
            await _stdin.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            pending.Reply.TrySetException(new RemoteHostException("The session connection closed."));
        }
        finally
        {
            _write.Release();
        }
        return await pending.Reply.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    private async Task ReadLoopAsync()
    {
        var stdout = _process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        List<string>? block = null;
        try
        {
            int n;
            while ((n = await stdout.ReadAsync(buffer)) > 0)
            {
                var span = buffer.AsSpan(0, n);
                while (!span.IsEmpty)
                {
                    var nl = span.IndexOf((byte)'\n');
                    if (nl < 0)
                    {
                        line.Write(span);
                        break;
                    }
                    line.Write(span[..nl]);
                    span = span[(nl + 1)..];
                    HandleLine(line.GetBuffer().AsSpan(0, (int)line.Length), ref block);
                    line.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_lock)
            {
                while (_pending.TryDequeue(out var p))
                {
                    p.Reply.TrySetException(new RemoteHostException("The session ended."));
                }
            }
            _output.Writer.TryComplete();
        }
    }

    private void HandleLine(ReadOnlySpan<byte> raw, ref List<string>? block)
    {
        if (!raw.IsEmpty && raw[^1] == (byte)'\r')
        {
            raw = raw[..^1];
        }
        if (block is not null)
        {
            if (raw.StartsWith("%end "u8) || raw.StartsWith("%error "u8))
            {
                var failed = raw.StartsWith("%error "u8);
                CompleteBlock(block, failed);
                block = null;
                return;
            }
            block.Add(Encoding.UTF8.GetString(raw));
            return;
        }
        if (raw.StartsWith("%begin "u8))
        {
            block = [];
            return;
        }
        if (TmuxProtocol.TryParseOutput(raw, out var pane, out var payload))
        {
            if (pane != _pane)
            {
                return;
            }
            var bytes = TmuxProtocol.Unescape(payload);
            lock (_lock)
            {
                switch (_mode)
                {
                    case Mode.Live:
                        _output.Writer.TryWrite(bytes);
                        break;
                    case Mode.Hold:
                        _held.Add(bytes);
                        break;
                }
            }
            return;
        }
        if (raw.StartsWith("%exit"u8))
        {
            _output.Writer.TryComplete();
        }
        // Other notifications (%session-changed, %layout-change, ...) are not needed here.
    }

    private void CompleteBlock(List<string> lines, bool failed)
    {
        Pending? pending = null;
        lock (_lock)
        {
            if (!_firstBlockSeen)
            {
                _firstBlockSeen = true; // the reply to the attach command itself
                return;
            }
            if (_pending.TryDequeue(out var p))
            {
                pending = p;
                if (p.HoldAfter && !failed)
                {
                    _mode = Mode.Hold;
                }
            }
        }
        if (pending is null)
        {
            return;
        }
        if (failed)
        {
            pending.Reply.TrySetException(new RemoteHostException(string.Join(' ', lines)));
        }
        else
        {
            pending.Reply.TrySetResult(lines);
        }
    }

    private async Task DrainErrorsAsync()
    {
        try
        {
            var text = await _process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _log.LogWarning("Session helper said: {Text}", text.Trim());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        try
        {
            // Detaching leaves the session running; closing stdin ends the control client.
            await _stdin.WriteAsync("detach-client\n"u8.ToArray());
            await _stdin.FlushAsync();
            _stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
        _process.Dispose();
        _output.Writer.TryComplete();
    }
}
