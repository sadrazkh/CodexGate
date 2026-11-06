using CodexGate.Data;
using CodexGate.Gateway;

namespace CodexGate.Api;

public sealed record LoginRequest(string? Username, string? Password);
public sealed record ChangePasswordRequest(string? Current, string? Next);
public sealed record NewKeyRequest(string? Name);
public sealed record UserInput(
    string? Username, string? DisplayName, string? Password, string? Role, long? PlanId, bool? Enabled,
    long? ExpiresAt, bool? NoExpiry, int? ExpiresInDays, string? Note);
public sealed record RenewRequest(int Days);
public sealed record PlanInput(string? Name, int? MaxConcurrency, string? AllowedModels, List<LimitInput>? Limits);
public sealed record LimitInput(int WindowHours, string? Kind, double Amount);
public sealed record PriceInput(string? Model, double InputPerM, double CachedPerM, double OutputPerM);
public sealed record SettingsInput(string? DefaultModel, string? PublicUrl, bool? AllowWebSearch = null, double? WebSearchPrice = null);

public sealed record WindowView(int WindowHours, string Kind, double Amount, double Used, double Percent, bool Exhausted, long? FreesAt)
{
    public static WindowView From(WindowStatus w) =>
        new(w.WindowHours, w.Kind, w.Amount, Math.Round(w.Used, 6), Math.Round(w.Percent, 2), w.Exhausted, w.FreesAt);
}

public sealed record LimitView(int WindowHours, string Kind, double Amount);

public sealed record PlanView(long Id, string Name, int MaxConcurrency, List<string> AllowedModels, List<LimitView> Limits, long Users)
{
    public static PlanView From(PlanRow p, long users = 0) => new(
        p.Id, p.Name, p.MaxConcurrency, ModelPatterns.Parse(p.AllowedModels),
        p.Limits.Select(l => new LimitView(l.WindowHours, l.Kind, l.Amount)).ToList(), users);
}

public sealed record GatewayView(string BaseUrl, string DefaultModel, string ProviderId, string EnvKey, int ActiveKeys);

public sealed record MeView(
    long Id, string Username, string DisplayName, string Role, bool Enabled, long? ExpiresAt, string Access,
    PlanView? Plan, List<WindowView> Quota, GatewayView Gateway);

public sealed record KeyView(long Id, string Name, string Prefix, long CreatedAt, long? LastUsedAt, long? RevokedAt)
{
    public static KeyView From(ApiKeyRow k) => new(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt);
}

public sealed record UsageView(
    long At, string Model, string Endpoint, int Status, long InputTokens, long CachedTokens, long OutputTokens,
    long ReasoningTokens, double BillableTokens, double? CostUsd, bool Estimated, long WebSearches, long DurationMs, string? Username)
{
    public static UsageView From(UsageRow u, string? username = null) => new(
        u.At, u.Model, u.Endpoint, u.Status, u.InputTokens, u.CachedTokens, u.OutputTokens, u.ReasoningTokens,
        Math.Round(u.BillableTokens, 2), u.CostUsd is { } c ? Math.Round(c, 6) : null, u.Estimated, u.ToolCalls, u.DurationMs, username);
}

public sealed record AdminUserView(
    long Id, string Username, string DisplayName, string Role, bool Enabled, long? PlanId, string? PlanName,
    long? ExpiresAt, string Access, string Note, long CreatedAt, long? LastLoginAt, long? LastUsedAt, long ActiveKeys,
    List<WindowView> Quota);

/// <summary>Builds what the panel shows for an account; shared by the user's and the admin's views.</summary>
public sealed class PanelViews(
    PlanStore plans, KeyStore keys, QuotaService quota, SettingsStore settings, TimeProvider time)
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
        var gateway = new GatewayView(
            BaseUrl(all.GetValueOrDefault(SettingsStore.PublicUrl), request), defaultModel, ProviderId, EnvKey,
            (int)await keys.CountActiveAsync(user.Id));
        return new MeView(
            user.Id, user.Username, user.DisplayName, user.Role, user.Enabled, user.ExpiresAt,
            Access.Code(Access.Check(user, plan, now)), plan is null ? null : PlanView.From(plan),
            windows.Select(WindowView.From).ToList(), gateway);
    }

    public static string BaseUrl(string? publicUrl, HttpRequest request)
    {
        var root = string.IsNullOrWhiteSpace(publicUrl)
            ? $"{request.Scheme}://{request.Host}{request.PathBase}"
            : publicUrl.Trim();
        return root.TrimEnd('/') + "/v1";
    }
}
