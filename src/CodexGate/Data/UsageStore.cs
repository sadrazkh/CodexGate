using Dapper;

namespace CodexGate.Data;

public sealed class UsageStore(Database db)
{
    private const string Columns = """
        id, user_id, key_id, at, model, upstream, endpoint, status, input_tokens, cached_tokens, output_tokens,
        reasoning_tokens, billable_tokens, cost_usd, estimated, tool_calls, duration_ms
        """;

    public async Task InsertAsync(UsageRow u)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("""
            INSERT INTO usage (user_id, key_id, at, model, upstream, endpoint, status, input_tokens, cached_tokens,
                output_tokens, reasoning_tokens, billable_tokens, cost_usd, estimated, tool_calls, duration_ms)
            VALUES (@UserId, @KeyId, @At, @Model, @Upstream, @Endpoint, @Status, @InputTokens, @CachedTokens,
                @OutputTokens, @ReasoningTokens, @BillableTokens, @CostUsd, @Estimated, @ToolCalls, @DurationMs)
            """, u);
    }

    /// <summary>Sum of billable tokens (kind "tokens") or dollars (kind "usd") since a time, plus the oldest row counted.</summary>
    public async Task<(double Used, long? Oldest)> SumSinceAsync(long userId, long since, string kind)
    {
        await using var c = await db.OpenAsync();
        var column = kind == LimitKinds.Usd ? "COALESCE(cost_usd, 0)" : "billable_tokens";
        var row = await c.QuerySingleAsync<(double Used, long? Oldest)>(
            $"SELECT COALESCE(SUM({column}), 0) AS used, MIN(at) AS oldest FROM usage WHERE user_id = @userId AND at >= @since",
            new { userId, since });
        return row;
    }

    /// <summary>What one finished request of this user has cost lately (tokens counted, dollars); null without history.</summary>
    public async Task<(double Tokens, double Usd)?> AverageRecentAsync(long userId, long since)
    {
        await using var c = await db.OpenAsync();
        var row = await c.QuerySingleAsync<(long Count, double Tokens, double Usd)>("""
            SELECT COUNT(*), COALESCE(SUM(billable_tokens), 0), COALESCE(SUM(cost_usd), 0)
            FROM usage WHERE user_id = @userId AND at >= @since AND status BETWEEN 200 AND 299
            """, new { userId, since });
        return row.Count == 0 ? null : (row.Tokens / row.Count, row.Usd / row.Count);
    }

    public async Task<List<UsageRow>> RecentAsync(long? userId, int limit)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<UsageRow>(
            $"SELECT {Columns} FROM usage WHERE (@userId IS NULL OR user_id = @userId) ORDER BY at DESC, id DESC LIMIT @limit",
            new { userId, limit })).ToList();
    }

    public async Task<List<UsageRow>> SinceAsync(long since)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<UsageRow>(
            $"SELECT {Columns} FROM usage WHERE at >= @since ORDER BY at, id", new { since })).ToList();
    }

    public async Task<List<DailyUsage>> DailyAsync(long? userId, long since, long offsetMs)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<DailyUsage>("""
            SELECT (at + @offsetMs) / 86400000 AS day, COUNT(*) AS requests,
                   COALESCE(SUM(billable_tokens), 0) AS tokens, COALESCE(SUM(cost_usd), 0) AS cost_usd
            FROM usage WHERE (@userId IS NULL OR user_id = @userId) AND at >= @since
            GROUP BY day ORDER BY day
            """, new { userId, since, offsetMs })).ToList();
    }

    public async Task<UsageTotals> TotalsAsync(long since)
    {
        await using var c = await db.OpenAsync();
        return await c.QuerySingleAsync<UsageTotals>("""
            SELECT COUNT(*) AS requests, COALESCE(SUM(billable_tokens), 0) AS tokens, COALESCE(SUM(cost_usd), 0) AS cost_usd,
                   COALESCE(SUM(input_tokens), 0) AS input_tokens, COALESCE(SUM(cached_tokens), 0) AS cached_tokens,
                   COALESCE(SUM(output_tokens), 0) AS output_tokens
            FROM usage WHERE at >= @since
            """, new { since });
    }

    public async Task<List<GroupUsage>> ByUserAsync(long since)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<GroupUsage>("""
            SELECT CAST(user_id AS TEXT) AS name, COUNT(*) AS requests, COALESCE(SUM(billable_tokens), 0) AS tokens,
                   COALESCE(SUM(cost_usd), 0) AS cost_usd, MAX(at) AS last_at
            FROM usage WHERE at >= @since GROUP BY user_id ORDER BY tokens DESC
            """, new { since })).ToList();
    }

    public async Task<List<GroupUsage>> ByModelAsync(long since)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<GroupUsage>("""
            SELECT model AS name, COUNT(*) AS requests, COALESCE(SUM(billable_tokens), 0) AS tokens,
                   COALESCE(SUM(cost_usd), 0) AS cost_usd, MAX(at) AS last_at
            FROM usage WHERE at >= @since GROUP BY model ORDER BY tokens DESC
            """, new { since })).ToList();
    }

    public async Task<Dictionary<long, long>> LastUsedByUserAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<(long UserId, long LastAt)>("SELECT user_id, MAX(at) FROM usage GROUP BY user_id"))
            .ToDictionary(r => r.UserId, r => r.LastAt);
    }
}

public sealed class DailyUsage
{
    public long Day { get; set; }
    public long Requests { get; set; }
    public double Tokens { get; set; }
    public double CostUsd { get; set; }
}

public sealed class UsageTotals
{
    public long Requests { get; set; }
    public double Tokens { get; set; }
    public double CostUsd { get; set; }
    public long InputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long OutputTokens { get; set; }
}

public sealed class GroupUsage
{
    public string Name { get; set; } = "";
    public long Requests { get; set; }
    public double Tokens { get; set; }
    public double CostUsd { get; set; }
    public long? LastAt { get; set; }
}
