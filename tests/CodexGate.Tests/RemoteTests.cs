using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexGate.Data;
using CodexGate.Remote;

namespace CodexGate.Tests;

/// <summary>Stands in for tmux and the root helper.</summary>
public sealed class FakeRemoteHost : IRemoteHost
{
    private readonly Lock _lock = new();
    public List<(long UserId, string Url, string Model, bool OwnLogin)> Provisions { get; } = [];
    public List<(long UserId, string TmuxName, string Key)> Starts { get; } = [];
    public Dictionary<long, HashSet<string>> Live { get; } = [];
    public List<string> Killed { get; } = [];
    public FakeLink? LastLink { get; private set; }
    public bool Broken { get; set; }

    public Task ProvisionAsync(long userId, string gatewayUrl, string model, bool ownLogin, CancellationToken ct)
    {
        lock (_lock)
        {
            Provisions.Add((userId, gatewayUrl, model, ownLogin));
        }
        return Task.CompletedTask;
    }

    public Task StartAsync(long userId, RemoteSessionRow session, int cols, int rows, int memoryMb, string key, CancellationToken ct)
    {
        lock (_lock)
        {
            Starts.Add((userId, session.TmuxName, key));
            if (!Live.TryGetValue(userId, out var set))
            {
                Live[userId] = set = [];
            }
            set.Add(session.TmuxName);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RemoteLive>> ListAsync(long userId, CancellationToken ct)
    {
        if (Broken)
        {
            throw new RemoteHostException("helper missing");
        }
        lock (_lock)
        {
            IReadOnlyList<RemoteLive> list = Live.TryGetValue(userId, out var set)
                ? set.Select(n => new RemoteLive(n, 0, 0)).ToList()
                : [];
            return Task.FromResult(list);
        }
    }

    public Task KillAsync(long userId, string tmuxName, CancellationToken ct)
    {
        lock (_lock)
        {
            Killed.Add($"{userId}:{tmuxName}");
            if (Live.TryGetValue(userId, out var set))
            {
                set.Remove(tmuxName);
            }
        }
        return Task.CompletedTask;
    }

    public Task<ITerminalLink> AttachAsync(long userId, string tmuxName, int cols, int rows, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!Live.TryGetValue(userId, out var set) || !set.Contains(tmuxName))
            {
                throw new RemoteHostException("no such session");
            }
        }
        LastLink = new FakeLink(cols, rows);
        return Task.FromResult<ITerminalLink>(LastLink);
    }
}

public sealed class FakeLink : ITerminalLink
{
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>();

    public FakeLink(int cols, int rows)
    {
        Size = (cols, rows);
        _out.Writer.TryWrite(Encoding.UTF8.GetBytes("welcome\r\n"));
    }

    public (int Cols, int Rows) Size { get; private set; }
    public List<string> Inputs { get; } = [];
    public bool Disposed { get; private set; }
    public ChannelReader<byte[]> Output => _out.Reader;

    public Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetString(data.Span);
        Inputs.Add(text);
        _out.Writer.TryWrite(Encoding.UTF8.GetBytes("echo:" + text));
        return Task.CompletedTask;
    }

    public Task ResizeAsync(int cols, int rows, CancellationToken ct)
    {
        Size = (cols, rows);
        return Task.CompletedTask;
    }

    public void End() => _out.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _out.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public sealed class RemoteTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<(HttpClient Admin, HttpClient User, long UserId, long PlanId)> SetupAsync(bool remote = true, int maxSessions = 2)
    {
        var admin = await _app.AdminAsync();
        await admin.PutAsJsonAsync("/api/admin/settings", new { publicUrl = "", defaultModel = "gpt-5.5" });
        var planId = await GateFactory.CreatePlanAsync(admin, new { name = "Server", remoteEnabled = remote, remoteMaxSessions = maxSessions });
        var userId = await GateFactory.CreateUserAsync(admin, "ali", planId);
        var user = await _app.SignInAsync("ali", "user-password-1");
        return (admin, user, userId, planId);
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient user, string dir = "project", string program = "codex") =>
        user.PostAsJsonAsync("/api/remote/sessions", new { name = "Work", dir, program, cols = 100, rows = 30 });

    private async Task<WebSocket> ConnectAsync(HttpClient user, long id, string? origin = "http://localhost", string? ticket = null)
    {
        ticket ??= (await GateFactory.JsonAsync(await user.PostAsync($"/api/remote/sessions/{id}/ticket", null))).GetProperty("ticket").GetString();
        var client = _app.Server.CreateWebSocketClient();
        if (origin is not null)
        {
            client.ConfigureRequest = r => r.Headers.Origin = origin;
        }
        return await client.ConnectAsync(new Uri($"ws://localhost/api/remote/sessions/{id}/terminal?ticket={Uri.EscapeDataString(ticket!)}&cols=90&rows=25"), CancellationToken.None);
    }

    private static async Task<string> ReceiveUntilAsync(WebSocket ws, string wanted)
    {
        var all = new StringBuilder();
        var buffer = new byte[4096];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!all.ToString().Contains(wanted, StringComparison.Ordinal))
        {
            var r = await ws.ReceiveAsync(buffer, timeout.Token);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                break;
            }
            all.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
        }
        return all.ToString();
    }

    [Fact]
    public async Task Only_plans_with_server_sessions_see_and_use_them()
    {
        var (_, user, _, _) = await SetupAsync(remote: false);
        Assert.False((await GateFactory.JsonAsync(await user.GetAsync("/api/me/"))).GetProperty("remoteAvailable").GetBoolean());
        Assert.False((await GateFactory.JsonAsync(await user.GetAsync("/api/remote/"))).GetProperty("available").GetBoolean());
        var res = await StartAsync(user);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("remote_not_allowed", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Each_session_gets_its_own_key_and_the_account_its_config()
    {
        var (_, user, userId, _) = await SetupAsync();
        Assert.True((await GateFactory.JsonAsync(await user.GetAsync("/api/me/"))).GetProperty("remoteAvailable").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await StartAsync(user)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StartAsync(user, "other", "shell")).StatusCode);

        Assert.Equal(2, _app.Remote.Provisions.Count);
        var first = _app.Remote.Provisions[0];
        Assert.Equal(userId, first.UserId);
        Assert.Equal("http://127.0.0.1:8080/v1", first.Url);
        Assert.Equal("gpt-5.5", first.Model);
        Assert.False(first.OwnLogin);
        Assert.Equal(2, _app.Remote.Starts.Count);
        Assert.All(_app.Remote.Starts, st => Assert.StartsWith("cgk_", st.Key));
        Assert.NotEqual(_app.Remote.Starts[0].Key, _app.Remote.Starts[1].Key);

        // Session keys are the sessions' own: not in the user's key list, not counted against their ten.
        var keys = await GateFactory.JsonAsync(await user.GetAsync("/api/me/keys"));
        Assert.Equal(0, keys.GetArrayLength());

        var list = await GateFactory.JsonAsync(await user.GetAsync("/api/remote/"));
        Assert.Equal(2, list.GetProperty("sessions").GetArrayLength());
        Assert.All(list.GetProperty("sessions").EnumerateArray(), s => Assert.True(s.GetProperty("running").GetBoolean()));

        var third = await StartAsync(user, "third");
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Contains("too_many_sessions", await third.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_sessions_usage_is_its_own_and_its_key_dies_with_it()
    {
        var (_, user, _, _) = await SetupAsync(maxSessions: 5);
        var id = (await GateFactory.JsonAsync(await StartAsync(user))).GetProperty("id").GetInt64();
        var key = _app.Remote.Starts.Single().Key;
        using var codex = _app.CreateClient();
        codex.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);

        var session = (await GateFactory.JsonAsync(await user.GetAsync("/api/remote/"))).GetProperty("sessions")[0];
        Assert.Equal(84, session.GetProperty("tokens").GetDouble()); // 60 fresh + 40 cached × 0.1 + 20 out

        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/remote/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
    }

    [Theory]
    [InlineData("../etc", "codex", "invalid_dir")]
    [InlineData(".hidden", "codex", "invalid_dir")]
    [InlineData("a b", "codex", "invalid_dir")]
    [InlineData("ok", "python", "invalid_program")]
    public async Task Refuses_bad_session_input(string dir, string program, string code)
    {
        var (_, user, _, _) = await SetupAsync();
        var res = await StartAsync(user, dir, program);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(code, await res.Content.ReadAsStringAsync());
        Assert.Empty(_app.Remote.Provisions);
    }

    [Fact]
    public async Task The_terminal_socket_carries_screen_input_and_size()
    {
        var (_, user, _, _) = await SetupAsync();
        var id = (await GateFactory.JsonAsync(await StartAsync(user))).GetProperty("id").GetInt64();

        using var ws = await ConnectAsync(user, id);
        Assert.Contains("welcome", await ReceiveUntilAsync(ws, "welcome"));
        Assert.Equal((90, 25), _app.Remote.LastLink!.Size);

        await ws.SendAsync(Encoding.UTF8.GetBytes("ls -la\r"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Contains("echo:ls -la", await ReceiveUntilAsync(ws, "echo:ls -la"));

        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"resize\",\"cols\":132,\"rows\":40}"), WebSocketMessageType.Text, true, CancellationToken.None);
        await ws.SendAsync("x"u8.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReceiveUntilAsync(ws, "echo:x");
        Assert.Equal((132, 40), _app.Remote.LastLink.Size);

        _app.Remote.LastLink.End(); // the session ends: the socket closes
        var buffer = new byte[256];
        var r = await ws.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(WebSocketMessageType.Close, r.MessageType);
        Assert.Equal("The session ended.", ws.CloseStatusDescription);
    }

    [Fact]
    public async Task Closing_the_browser_detaches_and_answers_the_close()
    {
        var (_, user, _, _) = await SetupAsync();
        var id = (await GateFactory.JsonAsync(await StartAsync(user))).GetProperty("id").GetInt64();
        using var ws = await ConnectAsync(user, id);
        await ReceiveUntilAsync(ws, "welcome");
        var link = _app.Remote.LastLink!;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "tab closed", timeout.Token); // waits for the server's answer
        Assert.Equal(WebSocketState.Closed, ws.State);
        for (var i = 0; i < 50 && !link.Disposed; i++)
        {
            await Task.Delay(20);
        }
        Assert.True(link.Disposed); // the tmux client is let go; the session itself keeps running
        Assert.True((await GateFactory.JsonAsync(await user.GetAsync("/api/remote/"))).GetProperty("sessions")[0].GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task Tickets_work_once_and_only_for_their_own_session()
    {
        var (admin, user, _, planId) = await SetupAsync();
        var id = (await GateFactory.JsonAsync(await StartAsync(user))).GetProperty("id").GetInt64();
        var ticket = (await GateFactory.JsonAsync(await user.PostAsync($"/api/remote/sessions/{id}/ticket", null))).GetProperty("ticket").GetString();

        using (await ConnectAsync(user, id, ticket: ticket))
        {
        }
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(user, id, ticket: ticket));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(user, id, ticket: "made-up"));
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(user, id, origin: "https://evil.example"));

        await GateFactory.CreateUserAsync(admin, "reza", planId);
        var reza = await _app.SignInAsync("reza", "user-password-1");
        Assert.Equal(HttpStatusCode.NotFound, (await reza.PostAsync($"/api/remote/sessions/{id}/ticket", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reza.DeleteAsync($"/api/remote/sessions/{id}")).StatusCode);
        var rezaSession = (await GateFactory.JsonAsync(await StartAsync(reza))).GetProperty("id").GetInt64();
        var rezaTicket = (await GateFactory.JsonAsync(await reza.PostAsync($"/api/remote/sessions/{rezaSession}/ticket", null))).GetProperty("ticket").GetString();
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(user, id, ticket: rezaTicket)); // a ticket for another session
    }

    [Fact]
    public async Task Stopping_and_deleting_end_the_sessions()
    {
        var (admin, user, userId, _) = await SetupAsync();
        var id = (await GateFactory.JsonAsync(await StartAsync(user))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await user.DeleteAsync($"/api/remote/sessions/{id}")).StatusCode);
        Assert.Contains($"{userId}:s{id}", _app.Remote.Killed);
        Assert.Equal(0, (await GateFactory.JsonAsync(await user.GetAsync("/api/remote/"))).GetProperty("sessions").GetArrayLength());

        var second = (await GateFactory.JsonAsync(await StartAsync(user))).GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{userId}")).StatusCode);
        Assert.Contains($"{userId}:s{second}", _app.Remote.Killed);
    }

    [Fact]
    public async Task A_missing_helper_is_reported_not_crashed()
    {
        var (_, user, _, _) = await SetupAsync();
        _app.Remote.Broken = true;
        var res = await user.GetAsync("/api/remote/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("remote_unavailable", await res.Content.ReadAsStringAsync());
    }
}

public sealed class RemoteDisabledTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task With_the_feature_off_nothing_is_offered()
    {
        _app.Settings["Gate:Remote:Enabled"] = "false";
        var admin = await _app.AdminAsync();
        var planId = await GateFactory.CreatePlanAsync(admin, new { name = "Server", remoteEnabled = true });
        await GateFactory.CreateUserAsync(admin, "ali", planId);
        var user = await _app.SignInAsync("ali", "user-password-1");
        Assert.False((await GateFactory.JsonAsync(await user.GetAsync("/api/me/"))).GetProperty("remoteAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/remote/sessions", new { name = "x", dir = "x", program = "codex" })).StatusCode);
    }
}

public class TmuxProtocolTests
{
    [Fact]
    public void Unescapes_octal_and_keeps_utf8()
    {
        var input = Encoding.UTF8.GetBytes("a\\033[1mB\\015\\012\\134 سلام");
        Assert.Equal("a\u001b[1mB\r\n\\ سلام", Encoding.UTF8.GetString(TmuxProtocol.Unescape(input)));
        Assert.Equal("\\09x", Encoding.UTF8.GetString(TmuxProtocol.Unescape("\\09x"u8))); // not octal: kept as is
    }

    [Fact]
    public void Parses_output_lines()
    {
        Assert.True(TmuxProtocol.TryParseOutput("%output %12 hello world"u8, out var pane, out var payload));
        Assert.Equal("%12", pane);
        Assert.Equal("hello world", Encoding.UTF8.GetString(payload));
        Assert.False(TmuxProtocol.TryParseOutput("%begin 1 2 0"u8, out _, out _));
    }

    [Fact]
    public void Types_ascii_as_hex_and_other_text_literally()
    {
        var commands = TmuxProtocol.SendKeys("%3", Encoding.UTF8.GetBytes("ls\r\u001b[Aسلام'x"));
        Assert.Equal(
        [
            "send-keys -t %3 -H 6c 73 0d 1b 5b 41",
            "send-keys -t %3 -l 'سلام'",
            "send-keys -t %3 -H 27 78",
        ], commands);
    }

    [Fact]
    public void Long_input_is_split_and_bad_utf8_dropped()
    {
        var commands = TmuxProtocol.SendKeys("%1", Encoding.ASCII.GetBytes(new string('a', 600)));
        Assert.Equal(3, commands.Count);
        Assert.Empty(TmuxProtocol.SendKeys("%1", [0xC3]));
    }

    [Fact]
    public void Draws_the_captured_screen_then_places_the_cursor()
    {
        var bytes = TmuxProtocol.InitialScreen(["one", "two"], 4, 1);
        Assert.Equal("\u001b[H\u001b[2Jone\r\ntwo\u001b[2;5H", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Session_names_follow_the_row_id() => Assert.Equal("s42", new RemoteSessionRow { Id = 42 }.TmuxName);
}
