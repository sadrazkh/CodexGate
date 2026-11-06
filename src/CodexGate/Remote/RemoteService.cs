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
    /// Makes sure the user's server account exists with a working gateway key and current settings.
    /// A new key is made only when the account has none or the user revoked it.
    /// </summary>
    public async Task PrepareAccountAsync(UserRow user, CancellationToken ct)
    {
        var all = await settings.GetAllAsync();
        var model = all.GetValueOrDefault(SettingsStore.DefaultModel);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gpt-5.5";
        }
        var gateway = options.Value.Remote.GatewayUrl.TrimEnd('/');
        var keyId = await store.AccountKeyAsync(user.Id);
        var active = keyId is { } k && (await keys.ListForUserAsync(user.Id)).Any(x => x.Id == k && x.RevokedAt is null);
        if (active)
        {
            await host.ProvisionAsync(user.Id, gateway, model, null, ct);
            return;
        }
        var secret = ApiKeys.Generate();
        var row = new ApiKeyRow
        {
            UserId = user.Id,
            Name = KeyName,
            Prefix = ApiKeys.DisplayPrefix(secret),
            Hash = ApiKeys.HashOf(secret),
            CreatedAt = Now(),
        };
        row.Id = await keys.InsertAsync(row);
        await host.ProvisionAsync(user.Id, gateway, model, secret, ct);
        await store.SetAccountAsync(user.Id, row.Id, Now());
    }

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
