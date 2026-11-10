using CodexGate.Data;
using CodexGate.Gateway;

namespace CodexGate.Api;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? Current, string? Next);
public sealed record NewKeyRequest(string? Name, long? DeviceId = null);
public sealed record DeviceInput(string? Name, List<LimitInput>? Limits);
public sealed record UserInput(
    string? Username, string? DisplayName, string? Password, string? Role, long? PlanId, bool? Enabled,
    long? ExpiresAt, bool? NoExpiry, int? ExpiresInDays, string? Note);
public sealed record RenewRequest(int Days);
public sealed record PlanInput(
    string? Name, int? MaxConcurrency, string? AllowedModels, List<LimitInput>? Limits,
    bool? RemoteEnabled = null, int? RemoteMaxSessions = null, int? RemoteMemoryMb = null, bool? OwnLogin = null);
public sealed record LimitInput(int WindowHours, string? Kind, double Amount);
public sealed record PriceInput(string? Model, double InputPerM, double CachedPerM, double OutputPerM);
public sealed record SettingsInput(
    string? DefaultModel, string? PublicUrl, bool? AllowWebSearch = null, double? WebSearchPrice = null, string? OpenAiKey = null);

public sealed record WindowView(int WindowHours, string Kind, double Amount, double Used, double Percent, bool Exhausted, long? FreesAt)
{
    public static WindowView From(WindowStatus w) =>
        new(w.WindowHours, w.Kind, w.Amount, Math.Round(w.Used, 6), Math.Round(w.Percent, 2), w.Exhausted, w.FreesAt);
}

public sealed record LimitView(int WindowHours, string Kind, double Amount);

public sealed record PlanView(
    long Id, string Name, int MaxConcurrency, List<string> AllowedModels, List<LimitView> Limits, long Users,
    bool RemoteEnabled, int RemoteMaxSessions, int RemoteMemoryMb, bool OwnLogin)
{
    public static PlanView From(PlanRow p, long users = 0) => new(
        p.Id, p.Name, p.MaxConcurrency, ModelPatterns.Parse(p.AllowedModels),
        p.Limits.Select(l => new LimitView(l.WindowHours, l.Kind, l.Amount)).ToList(), users,
        p.RemoteEnabled, p.RemoteMaxSessions, p.RemoteMemoryMb, p.OwnLogin);
}

/// <summary>How a user's Codex reaches the gateway. With OwnLogin, BaseUrl is the ChatGPT relay and the key goes in KeyHeader.</summary>
public sealed record GatewayView(
    string BaseUrl, string DefaultModel, string ProviderId, string EnvKey, int ActiveKeys, bool OwnLogin, string KeyHeader);

public sealed record DeviceView(
    long Id, string Name, List<LimitView> Limits, List<WindowView> Quota, long ActiveKeys, long Requests30, double Tokens30,
    double CostUsd30, long? LastAt)
{
    public static DeviceView From(DeviceRow d, List<WindowStatus> windows, long activeKeys, DeviceUsage? usage) => new(
        d.Id, d.Name, d.Limits.Select(l => new LimitView(l.WindowHours, l.Kind, l.Amount)).ToList(),
        windows.Select(WindowView.From).ToList(), activeKeys, usage?.Requests ?? 0, Math.Round(usage?.Tokens ?? 0, 2),
        Math.Round(usage?.CostUsd ?? 0, 6), usage?.LastAt);
}

public sealed record MeView(
    long Id, string Username, string DisplayName, string Role, bool Enabled, long? ExpiresAt, string Access,
    PlanView? Plan, List<WindowView> Quota, GatewayView Gateway, bool RemoteAvailable);

public sealed record KeyView(long Id, string Name, string Prefix, long CreatedAt, long? LastUsedAt, long? RevokedAt, long? DeviceId)
{
    public static KeyView From(ApiKeyRow k) => new(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt, k.DeviceId);
}

public sealed record UsageView(
    long At, string Model, string Endpoint, int Status, long InputTokens, long CachedTokens, long OutputTokens,
    long ReasoningTokens, double BillableTokens, double? CostUsd, bool Estimated, long WebSearches, long DurationMs, string? Username,
    string? Device = null)
{
    public static UsageView From(UsageRow u, string? username = null, string? device = null) => new(
        u.At, u.Model, u.Endpoint, u.Status, u.InputTokens, u.CachedTokens, u.OutputTokens, u.ReasoningTokens,
        Math.Round(u.BillableTokens, 2), u.CostUsd is { } c ? Math.Round(c, 6) : null, u.Estimated, u.ToolCalls, u.DurationMs, username,
        device);
}

public sealed record AdminUserView(
    long Id, string Username, string DisplayName, string Role, bool Enabled, long? PlanId, string? PlanName,
    long? ExpiresAt, string Access, string Note, long CreatedAt, long? LastLoginAt, long? LastUsedAt, long ActiveKeys,
    List<WindowView> Quota);

/// <summary>Builds what the panel shows for an account; shared by the user's and the admin's views.</summary>
public sealed class PanelViews(
    PlanStore plans, KeyStore keys, QuotaService quota, SettingsStore settings, TimeProvider time,
    Microsoft.Extensions.Options.IOptions<GateOptions> options)
{
    public const string ProviderId = "codexgate";
    public const string EnvKey = "CODEXGATE_API_KEY";

    public long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<MeView> MeAsync(UserRow user, HttpRequest request)
    {
        var now = Now();
        var plan = user.PlanId is { } id ? await plans.GetAsync(id) : null;
        var windows = plan is null ? [] : await quota.EvaluateAsync(user, plan.Limits, now);
        var all = await settings.GetAllAsync();
        var defaultModel = all.GetValueOrDefault(SettingsStore.DefaultModel) ?? "";
        var allowed = plan is null ? [] : ModelPatterns.Parse(plan.AllowedModels);
        if (defaultModel.Length == 0 || !ModelPatterns.Allowed(allowed, defaultModel))
        {
            defaultModel = allowed.FirstOrDefault(p => !p.Contains('*')) ?? defaultModel;
        }
        var ownLogin = plan is { OwnLogin: true };
        var root = BaseUrl(all.GetValueOrDefault(SettingsStore.PublicUrl), request);
        var gateway = new GatewayView(
            ownLogin ? ChatGptUrl(root) : root, defaultModel, ProviderId, EnvKey,
            (int)await keys.CountPersonalActiveAsync(user.Id), ownLogin, GatewayHandler.KeyHeader);
        return new MeView(
            user.Id, user.Username, user.DisplayName, user.Role, user.Enabled, user.ExpiresAt,
            Access.Code(Access.Check(user, plan, now)), plan is null ? null : PlanView.From(plan),
            windows.Select(WindowView.From).ToList(), gateway,
            options.Value.Remote.Enabled && plan is { RemoteEnabled: true });
    }

    public static string BaseUrl(string? publicUrl, HttpRequest request)
    {
        var root = string.IsNullOrWhiteSpace(publicUrl)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
            : publicUrl.Trim();
        return root.TrimEnd('/') + "/v1";
    }

    /// <summary>The ChatGPT relay next to a gateway address ending in /v1.</summary>
    public static string ChatGptUrl(string gatewayUrl)
    {
        var trimmed = gatewayUrl.TrimEnd('/');
        return (trimmed.EndsWith("/v1", StringComparison.Ordinal) ? trimmed[..^3] : trimmed) + "/chatgpt/v1";
    }
}
