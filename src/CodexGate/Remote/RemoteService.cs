using System.Collections.Concurrent;
using System.Security.Cryptography;
using CodexGate.Data;
using CodexGate.Gateway;
using CodexGate.Security;
using Microsoft.Extensions.Options;

namespace CodexGate.Remote;

/// <summary>Who may run server sessions, and the account and key each user's sessions use.</summary>
public sealed class RemoteService(
    IRemoteHost host, RemoteStore store, KeyStore keys, PlanStore plans, SettingsStore settings,
    IOptions<GateOptions> options, TimeProvider time)
{
    /// <summary>Name of the one key per account that versions before 0.4 made; it is revoked on the next start.</summary>
    public const string KeyName = "Server sessions";

    public bool Enabled => options.Value.Remote.Enabled;

    public IRemoteHost Host => host;

    /// <summary>The plan, when this user may use server sessions right now; null otherwise.</summary>
    public async Task<PlanRow?> AllowedPlanAsync(UserRow user)
    {
        if (!Enabled)
        {
            return null;
        }
        var plan = user.PlanId is { } id ? await plans.GetAsync(id) : null;
        return plan is { RemoteEnabled: true } && Access.Check(user, plan, Now()) == AccessProblem.None ? plan : null;
    }

    /// <summary>
    /// Makes sure the user's server account exists with current settings for the plan: the gateway's
    /// API key, or the ChatGPT relay with the user's own sign-in. Keys are per session (see
    /// <see cref="NewSessionKeyAsync"/>); an account-wide key from before 0.4 is revoked.
    /// </summary>
    public async Task PrepareAccountAsync(UserRow user, PlanRow plan, CancellationToken ct)
    {
        var all = await settings.GetAllAsync();
        var model = all.GetValueOrDefault(SettingsStore.DefaultModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-5.5";
        }
        var gateway = options.Value.Remote.GatewayUrl.TrimEnd('/');
        await host.ProvisionAsync(user.Id, plan.OwnLogin ? Api.PanelViews.ChatGptUrl(gateway) : gateway, model, plan.OwnLogin, ct);
        if (await store.AccountKeyAsync(user.Id) is { } old)
        {
            await keys.RevokeAsync(old, user.Id, Now());
            await store.SetAccountAsync(user.Id, null, Now());
        }
    }

    /// <summary>A key for one session only, so its usage can be told apart; it counts against the device if one is given.</summary>
    public async Task<(long Id, string Secret)> NewSessionKeyAsync(UserRow user, string sessionName, long? deviceId)
    {
        var secret = ApiKeys.Generate();
        var name = "Session: " + sessionName;
        var row = new ApiKeyRow
        {
            UserId = user.Id,
            Name = name.Length > 48 ? name[..48] : name,
            Prefix = ApiKeys.DisplayPrefix(secret),
            Hash = ApiKeys.HashOf(secret),
            CreatedAt = Now(),
            DeviceId = deviceId,
        };
        row.Id = await keys.InsertAsync(row);
        return (row.Id, secret);
    }

    public Task RevokeSessionKeyAsync(RemoteSessionRow session) =>
        session.KeyId is { } id ? keys.RevokeAsync(id, session.UserId, Now()) : Task.CompletedTask;

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>
/// One-time, short-lived tickets for opening a terminal socket. Getting one needs the panel cookie
/// and the CSRF header, so another site cannot open a user's terminal.
/// </summary>
public sealed class TerminalTickets(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (long UserId, long SessionId, DateTimeOffset Expires)> _tickets = new();

    public string Issue(long userId, long sessionId)
    {
        var now = time.GetUtcNow();
        foreach (var (k, v) in _tickets)
        {
            if (v.Expires < now)
            {
                _tickets.TryRemove(k, out _);
            }
        }
        var ticket = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _tickets[ticket] = (userId, sessionId, now + Lifetime);
        return ticket;
    }

    public (long UserId, long SessionId)? Redeem(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_tickets.TryRemove(ticket, out var t) || t.Expires < time.GetUtcNow())
        {
            return null;
        }
        return (t.UserId, t.SessionId);
    }
}
