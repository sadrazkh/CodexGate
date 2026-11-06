using System.Diagnostics;
using System.Globalization;
using CodexGate.Data;
using Microsoft.Extensions.Options;

namespace CodexGate.Remote;

/// <summary>
/// Talks to the root helper (deploy/codexgate-host) through sudo. The helper is the only code that
/// runs as root; it validates every argument and runs tmux as the user's own account (cg-&lt;id&gt;).
/// </summary>
public sealed class TmuxRemoteHost(IOptions<GateOptions> options, ILogger<TmuxRemoteHost> log) : IRemoteHost
{
    private RemoteOptions Settings => options.Value.Remote;

    public async Task ProvisionAsync(long userId, string gatewayUrl, string model, string? key, CancellationToken ct)
    {
        var (code, _, err) = await RunAsync(["provision", Id(userId), gatewayUrl, model], key is null ? "" : key + "\n", ct);
        if (code != 0)
        {
            throw new RemoteHostException("Could not prepare the server account: " + err.Trim());
        }
    }

    public async Task StartAsync(long userId, RemoteSessionRow session, int cols, int rows, int memoryMb, CancellationToken ct)
    {
        var (code, _, err) = await RunAsync(
            ["new", Id(userId), session.TmuxName, Num(cols), Num(rows), session.Dir, session.Program, Num(Math.Max(0, memoryMb))], null, ct);
        if (code != 0)
        {
            throw new RemoteHostException("Could not start the session: " + err.Trim());
        }
    }

    public async Task<IReadOnlyList<RemoteLive>> ListAsync(long userId, CancellationToken ct)
    {
        var (code, output, err) = await RunAsync(["list", Id(userId)], null, ct);
        if (code != 0)
        {
            throw new RemoteHostException("Could not list sessions: " + err.Trim());
        }
        var result = new List<RemoteLive>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ');
            if (parts.Length >= 3
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var created)
                && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var attached))
            {
                result.Add(new RemoteLive(parts[0], created * 1000, attached));
            }
        }
        return result;
    }

    public async Task KillAsync(long userId, string tmuxName, CancellationToken ct)
    {
        await RunAsync(["kill", Id(userId), tmuxName], null, ct);
    }

    public async Task<ITerminalLink> AttachAsync(long userId, string tmuxName, int cols, int rows, CancellationToken ct) =>
        await TmuxControlLink.AttachAsync(Start(["attach", Id(userId), tmuxName]), tmuxName, cols, rows, log, ct);

    private ProcessStartInfo Start(IEnumerable<string> args)
    {
        var start = new ProcessStartInfo { UseShellExecute = false };
        if (Settings.UseSudo)
        {
            start.FileName = "sudo";
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add(Settings.Helper);
        }
        else
        {
            start.FileName = Settings.Helper;
        }
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }
        // The helper gets nothing of the gateway's environment (upstream keys live there).
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        start.Environment["LANG"] = "C.UTF-8";
        return start;
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] args, string? stdin, CancellationToken ct)
    {
        var start = Start(args);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new RemoteHostException("Could not start the session helper.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RemoteHostException("The session helper is not installed: " + ex.Message);
        }
        using (process)
        {
            if (!string.IsNullOrEmpty(stdin))
            {
                await process.StandardInput.WriteAsync(stdin);
            }
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new RemoteHostException("The session helper did not answer in time.");
            }
            var (o, e) = (await output, await error);
            if (process.ExitCode != 0)
            {
                log.LogWarning("Session helper {Command} failed ({Code}): {Error}", args[0], process.ExitCode, e.Trim());
            }
            return (process.ExitCode, o, e);
        }
    }

    private static string Id(long userId) => userId.ToString(CultureInfo.InvariantCulture);

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
