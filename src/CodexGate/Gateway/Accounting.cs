using CodexGate.Data;

namespace CodexGate.Gateway;

/// <summary>Model names and patterns: exact (case-insensitive), or a prefix ending in "*", or "*" alone.</summary>
public static class ModelPatterns
{
    public static List<string> Parse(string? text) =>
        (text ?? "")
            .Split([',', '\n', '\r', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string Join(IEnumerable<string> patterns) => string.Join(", ", patterns);

    public static bool Matches(string pattern, string model)
    {
        if (pattern == "*")
        {
            return true;
        }
        return pattern.EndsWith('*')
            ? model.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, model, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An empty list allows every model.</summary>
    public static bool Allowed(IReadOnlyCollection<string> patterns, string model) =>
        patterns.Count == 0 || patterns.Any(p => Matches(p, model));

    public static bool IsValid(string pattern)
    {
        if (pattern.Length is 0 or > 128)
        {
            return false;
        }
        var star = pattern.IndexOf('*');
        if (star != -1 && star != pattern.Length - 1)
        {
            return false;
        }
        return pattern.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':' or '/' or '*' or '@');
    }
}

/// <summary>Token counts as reported by the Responses API.</summary>
public readonly record struct TokenUsage(long Input, long Cached, long Output, long Reasoning);

public static class UsageMath
{
    /// <summary>
    /// What a request counts against a token limit: fresh input and output in full,
    /// cached input at <paramref name="cachedWeight"/> (it is far cheaper upstream).
    /// </summary>
    public static double BillableTokens(TokenUsage u, double cachedWeight)
    {
        var cached = Math.Clamp(u.Cached, 0, Math.Max(0, u.Input));
        return Math.Max(0, u.Input - cached) + cached * Math.Clamp(cachedWeight, 0, 1) + Math.Max(0, u.Output);
    }

    /// <summary>Dollar cost from per-million prices; null when no price matches the model.</summary>
    public static double? Cost(TokenUsage u, PriceRow? price)
    {
        if (price is null)
        {
            return null;
        }
        var cached = Math.Clamp(u.Cached, 0, Math.Max(0, u.Input));
        var fresh = Math.Max(0, u.Input - cached);
        return (fresh * price.InputPerM + cached * price.CachedPerM + Math.Max(0, u.Output) * price.OutputPerM) / 1_000_000d;
    }

    /// <summary>Exact model first, then the longest matching "prefix*" pattern.</summary>
    public static PriceRow? FindPrice(IEnumerable<PriceRow> prices, string model)
    {
        PriceRow? best = null;
        foreach (var p in prices)
        {
            if (string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
            if (ModelPatterns.Matches(p.Model, model) && (best is null || p.Model.Length > best.Model.Length))
            {
                best = p;
            }
        }
        return best;
    }
}

public sealed record WindowStatus(int WindowHours, string Kind, double Amount, double Used, long? FreesAt)
{
    /// <summary>Set when this window is a device's own limit rather than the plan's.</summary>
    public string? Device { get; init; }

    public bool Exhausted => Used >= Amount;
    public double Percent => Amount <= 0 ? 100 : Math.Min(100, Used / Amount * 100);
}

/// <summary>
/// Rolling-window limits: each limit counts what was used in the last N hours, but nothing from
/// before an admin's usage reset.
/// </summary>
public sealed class QuotaService(UsageStore usage)
{
    /// <summary>Assumed for a request in flight when the user has no recent history.</summary>
    public const double FallbackTokens = 20_000;
    public const double FallbackUsd = 0.05;

    /// <param name="running">Other requests of this user still in flight; each counts at the user's recent average.</param>
    public async Task<List<WindowStatus>> EvaluateAsync(UserRow user, IEnumerable<PlanLimitRow> limits, long now, int running = 0)
    {
        var result = new List<WindowStatus>();
        (double Tokens, double Usd) perRequest = (0, 0);
        if (running > 0)
        {
            perRequest = await usage.AverageRecentAsync(user.Id, now - 86_400_000L) ?? (FallbackTokens, FallbackUsd);
        }
        foreach (var limit in limits.OrderBy(l => l.WindowHours))
        {
            var windowMs = limit.WindowHours * 3_600_000L;
            var since = Math.Max(now - windowMs, user.QuotaResetAt ?? 0);
            var (used, oldest) = await usage.SumSinceAsync(user.Id, since, limit.Kind);
            used += running * (limit.Kind == LimitKinds.Usd ? perRequest.Usd : perRequest.Tokens);
            result.Add(new WindowStatus(limit.WindowHours, limit.Kind, limit.Amount, used, oldest is null ? null : oldest + windowMs));
        }
        return result;
    }

    /// <summary>A device's own limits: what was used through its keys in each window.</summary>
    public async Task<List<WindowStatus>> EvaluateDeviceAsync(DeviceRow device, long now)
    {
        var result = new List<WindowStatus>();
        foreach (var limit in device.Limits.OrderBy(l => l.WindowHours))
        {
            var windowMs = limit.WindowHours * 3_600_000L;
            var (used, oldest) = await usage.SumSinceForDeviceAsync(device.Id, now - windowMs, limit.Kind);
            result.Add(new WindowStatus(limit.WindowHours, limit.Kind, limit.Amount, used, oldest is null ? null : oldest + windowMs)
            {
                Device = device.Name,
            });
        }
        return result;
    }
}

public enum AccessProblem
{
    None,
    AccountDisabled,
    SubscriptionExpired,
    NoPlan,
}

public static class Access
{
    public static AccessProblem Check(UserRow user, PlanRow? plan, long now) =>
        !user.Enabled ? AccessProblem.AccountDisabled
        : user.ExpiresAt is { } exp && exp <= now ? AccessProblem.SubscriptionExpired
        : plan is null ? AccessProblem.NoPlan
        : AccessProblem.None;

    public static string Code(AccessProblem p) => p switch
    {
        AccessProblem.AccountDisabled => "account_disabled",
        AccessProblem.SubscriptionExpired => "subscription_expired",
        AccessProblem.NoPlan => "no_plan",
        _ => "ok",
    };
}

/// <summary>Requests in flight, per user and in total (one process; not shared across replicas).</summary>
public sealed class ConcurrencyGate
{
    private readonly Dictionary<long, int> _inFlight = [];
    private readonly Lock _lock = new();
    private int _total;

    public enum Result
    {
        Ok,
        UserFull,
        GatewayFull,
    }

    public Result TryEnter(long userId, int max, int totalMax = int.MaxValue)
    {
        lock (_lock)
        {
            _inFlight.TryGetValue(userId, out var n);
            if (n >= Math.Max(1, max))
            {
                return Result.UserFull;
            }
            if (_total >= Math.Max(1, totalMax))
            {
                return Result.GatewayFull;
            }
            _inFlight[userId] = n + 1;
            _total++;
            return Result.Ok;
        }
    }

    public void Exit(long userId)
    {
        lock (_lock)
        {
            if (_inFlight.TryGetValue(userId, out var n))
            {
                _total = Math.Max(0, _total - 1);
                if (n <= 1)
                {
                    _inFlight.Remove(userId);
                }
                else
                {
                    _inFlight[userId] = n - 1;
                }
            }
        }
    }

    public int Current(long userId)
    {
        lock (_lock)
        {
            return _inFlight.GetValueOrDefault(userId);
        }
    }
}
