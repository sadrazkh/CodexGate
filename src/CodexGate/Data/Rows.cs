namespace CodexGate.Data;

// Plain rows as stored. Times are Unix milliseconds (UTC).

public sealed class UserRow
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.User;
    public long? PlanId { get; set; }
    public bool Enabled { get; set; } = true;
    public long? ExpiresAt { get; set; }
    public string Note { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? LastLoginAt { get; set; }

    /// <summary>Usage before this time no longer counts against the plan's limits (admin reset).</summary>
    public long? QuotaResetAt { get; set; }

    public bool IsAdmin => Role == Roles.Admin;
}

public static class Roles
{
    public const string Admin = "admin";
    public const string User = "user";

    public static bool IsValid(string? role) => role is Admin or User;
}

public sealed class PlanRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int MaxConcurrency { get; set; } = 2;
    public string AllowedModels { get; set; } = "";
    public long CreatedAt { get; set; }

    /// <summary>Whether users on this plan may run Codex sessions on the server.</summary>
    public bool RemoteEnabled { get; set; }
    public int RemoteMaxSessions { get; set; } = 2;

    /// <summary>Memory cap for a user's server sessions in MiB (0 = none); needs systemd on the host.</summary>
    public int RemoteMemoryMb { get; set; }
    public List<PlanLimitRow> Limits { get; set; } = [];
}

public sealed class RemoteSessionRow
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string Dir { get; set; } = "";
    public string Program { get; set; } = "codex";
    public long CreatedAt { get; set; }

    /// <summary>The tmux session name on the host.</summary>
    public string TmuxName => "s" + Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public static class LimitKinds
{
    public const string Tokens = "tokens";
    public const string Usd = "usd";

    public static bool IsValid(string? kind) => kind is Tokens or Usd;
}

public sealed class PlanLimitRow
{
    public long Id { get; set; }
    public long PlanId { get; set; }
    public int WindowHours { get; set; }
    public string Kind { get; set; } = LimitKinds.Tokens;
    public double Amount { get; set; }
}

public sealed class ApiKeyRow
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public byte[] Hash { get; set; } = [];
    public long CreatedAt { get; set; }
    public long? LastUsedAt { get; set; }
    public long? RevokedAt { get; set; }
}

public sealed class UsageRow
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long? KeyId { get; set; }
    public long At { get; set; }
    public string Model { get; set; } = "";
    public string Upstream { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public int Status { get; set; }
    public long InputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public double BillableTokens { get; set; }
    public double? CostUsd { get; set; }
    public bool Estimated { get; set; }

    /// <summary>Hosted web searches the upstream ran (and billed per call).</summary>
    public long ToolCalls { get; set; }
    public long DurationMs { get; set; }
}

public sealed class PriceRow
{
    public string Model { get; set; } = "";
    public double InputPerM { get; set; }
    public double CachedPerM { get; set; }
    public double OutputPerM { get; set; }
}
