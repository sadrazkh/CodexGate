using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexGate.Api;
using CodexGate.Data;
using CodexGate.Security;

namespace CodexGate.Remote;

public sealed record NewSessionInput(string? Name, string? Dir, string? Program, int? Cols, int? Rows);

public sealed record SessionView(long Id, string Name, string Dir, string Program, long CreatedAt, bool Running, int Attached);

/// <summary>Server sessions in the panel: list, start, stop, and the terminal socket.</summary>
public static partial class RemoteApi
{
    public static void MapRemoteApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/remote").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/sessions", StartAsync);
        group.MapDelete("/sessions/{id:long}", StopAsync);
        group.MapPost("/sessions/{id:long}/ticket", async (long id, HttpContext ctx, RemoteService remote, RemoteStore store, TerminalTickets tickets) =>
        {
            var user = ctx.CurrentUser()!;
            var session = await store.GetAsync(id);
            if (session is null || session.UserId != user.Id)
            {
                return Errors.NotFound();
            }
            if (await remote.AllowedPlanAsync(user) is null)
            {
                return Errors.Json(403, "remote_not_allowed");
            }
            return Results.Ok(new { ticket = tickets.Issue(user.Id, id) });
        });

        // The socket authenticates with a ticket from the call above, not the cookie.
        app.Map("/api/remote/sessions/{id:long}/terminal", TerminalAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, RemoteService remote, RemoteStore store, CancellationToken ct)
    {
        var user = ctx.CurrentUser()!;
        var plan = await remote.AllowedPlanAsync(user);
        if (plan is null)
        {
            return Results.Ok(new { available = false, featureEnabled = remote.Enabled, maxSessions = 0, memoryMb = 0, sessions = Array.Empty<SessionView>() });
        }
        var rows = await store.ListAsync(user.Id);
        IReadOnlyList<RemoteLive> live;
        try
        {
            live = await remote.Host.ListAsync(user.Id, ct);
        }
        catch (RemoteHostException ex)
        {
            return Errors.Json(503, "remote_unavailable", ex.Message);
        }
        var byName = live.ToDictionary(l => l.Name);
        return Results.Ok(new
        {
            available = true,
            featureEnabled = true,
            maxSessions = plan.RemoteMaxSessions,
            memoryMb = plan.RemoteMemoryMb,
            sessions = rows.Select(r => new SessionView(r.Id, r.Name, r.Dir, r.Program, r.CreatedAt,
                byName.ContainsKey(r.TmuxName), byName.TryGetValue(r.TmuxName, out var l) ? l.Attached : 0)),
        });
    }

    private static async Task<IResult> StartAsync(
        NewSessionInput input, HttpContext ctx, RemoteService remote, RemoteStore store, PanelViews views, CancellationToken ct)
    {
        var user = ctx.CurrentUser()!;
        var plan = await remote.AllowedPlanAsync(user);
        if (plan is null)
        {
            return Errors.Json(403, "remote_not_allowed");
        }
        var name = (input.Name ?? "").Trim();
        var dir = (input.Dir ?? "").Trim();
        var program = input.Program ?? "codex";
        if (name.Length is 0 or > 40 || name.Any(char.IsControl))
        {
            return Errors.Json(400, "invalid_session_name");
        }
        if (!DirName().IsMatch(dir))
        {
            return Errors.Json(400, "invalid_dir");
        }
        if (program is not ("codex" or "shell"))
        {
            return Errors.Json(400, "invalid_program");
        }
        var cols = Math.Clamp(input.Cols ?? 120, 20, 400);
        var rows = Math.Clamp(input.Rows ?? 36, 5, 200);
        try
        {
            var live = await remote.Host.ListAsync(user.Id, ct);
            var mine = (await store.ListAsync(user.Id)).Select(s => s.TmuxName).ToHashSet();
            if (live.Count(l => mine.Contains(l.Name)) >= plan.RemoteMaxSessions)
            {
                return Errors.Json(409, "too_many_sessions");
            }
            await remote.PrepareAccountAsync(user, ct);
            var row = new RemoteSessionRow { UserId = user.Id, Name = name, Dir = dir, Program = program, CreatedAt = views.Now() };
            row.Id = await store.InsertAsync(row);
            try
            {
                await remote.Host.StartAsync(user.Id, row, cols, rows, plan.RemoteMemoryMb, ct);
            }
            catch
            {
                await store.DeleteAsync(row.Id);
                throw;
            }
            return Results.Ok(new SessionView(row.Id, row.Name, row.Dir, row.Program, row.CreatedAt, true, 0));
        }
        catch (RemoteHostException ex)
        {
            return Errors.Json(503, "remote_unavailable", ex.Message);
        }
    }

    private static async Task<IResult> StopAsync(long id, HttpContext ctx, RemoteService remote, RemoteStore store, CancellationToken ct)
    {
        var user = ctx.CurrentUser()!;
        var session = await store.GetAsync(id);
        if (session is null || session.UserId != user.Id)
        {
            return Errors.NotFound();
        }
        if (remote.Enabled)
        {
            try
            {
                await remote.Host.KillAsync(user.Id, session.TmuxName, ct);
            }
            catch (RemoteHostException ex)
            {
                return Errors.Json(503, "remote_unavailable", ex.Message);
            }
        }
        await store.DeleteAsync(id);
        return Results.NoContent();
    }

    private static async Task TerminalAsync(
        long id, HttpContext ctx, TerminalTickets tickets, RemoteService remote, RemoteStore store, UserStore users,
        ILoggerFactory logs, int? cols, int? rows, string? ticket)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        // Belt and braces next to the ticket: a browser on another site sends its own Origin.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var redeemed = tickets.Redeem(ticket);
        var session = await store.GetAsync(id);
        var user = redeemed is { } r ? await users.GetAsync(r.UserId) : null;
        if (redeemed is null || session is null || user is null || redeemed.Value.SessionId != id || session.UserId != user.Id
            || await remote.AllowedPlanAsync(user) is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var log = logs.CreateLogger("CodexGate.Terminal");
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var ct = ctx.RequestAborted;
        ITerminalLink link;
        try
        {
            link = await remote.Host.AttachAsync(user.Id, session.TmuxName, Math.Clamp(cols ?? 120, 20, 400), Math.Clamp(rows ?? 36, 5, 200), ct);
        }
        catch (Exception ex) when (ex is RemoteHostException or TimeoutException)
        {
            log.LogWarning("Attach to {Session} failed: {Error}", session.TmuxName, ex.Message);
            await CloseAsync(socket, "The session is not running.");
            return;
        }

        // The pump has its own token so leaving the loop below can always stop it: the link's output
        // only ends when the session does, and waiting for that here would leave a dead socket and a
        // tmux client behind for every closed browser tab.
        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toBrowser = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in link.Output.ReadAllAsync(pumpStop.Token))
                {
                    // Not cancelled mid-frame: that would abort the socket instead of closing it cleanly.
                    await socket.SendAsync(chunk, WebSocketMessageType.Binary, true, CancellationToken.None);
                }
                await CloseAsync(socket, "The session ended.");
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }
        }, CancellationToken.None);

        try
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }
                        if (message.Length + result.Count > 1024 * 1024)
                        {
                            throw new WebSocketException("Message too large.");
                        }
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        await link.SendInputAsync(message.GetBuffer().AsMemory(0, (int)message.Length), ct);
                    }
                    else if (TryResize(message, out var c, out var rr))
                    {
                        await link.ResizeAsync(c, rr, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or RemoteHostException or TimeoutException)
            {
            }
        }
        finally
        {
            // Stop sending first (one send at a time on a socket), then detach from the session, which
            // keeps running, and only then answer the browser's close.
            await pumpStop.CancelAsync();
            await link.DisposeAsync();
            await toBrowser;
            await CloseAsync(socket, "Bye.");
        }
    }

    /// <summary>Text frames carry control messages: {"type":"resize","cols":120,"rows":36}.</summary>
    private static bool TryResize(MemoryStream message, out int cols, out int rows)
    {
        cols = rows = 0;
        try
        {
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.GetString() == "resize"
                && root.TryGetProperty("cols", out var c) && c.TryGetInt32(out cols)
                && root.TryGetProperty("rows", out var r) && r.TryGetInt32(out rows))
            {
                cols = Math.Clamp(cols, 20, 400);
                rows = Math.Clamp(rows, 5, 200);
                return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex DirName();
}
