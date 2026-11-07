using Dapper;

namespace CodexGate.Data;

public sealed class UserStore(Database db)
{
    private const string Columns =
        "id, username, display_name, password_hash, role, plan_id, enabled, expires_at, note, security_stamp, created_at, last_login_at, quota_reset_at";

    public async Task<UserRow?> GetAsync(long id)
    {
        await using var c = await db.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<UserRow>($"SELECT {Columns} FROM users WHERE id = @id", new { id });
    }

    public async Task<UserRow?> GetByUsernameAsync(string username)
    {
        await using var c = await db.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<UserRow>(
            $"SELECT {Columns} FROM users WHERE username = @username", new { username });
    }

    public async Task<List<UserRow>> ListAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<UserRow>($"SELECT {Columns} FROM users ORDER BY username")).ToList();
    }

    public async Task<long> CountAdminsAsync(bool enabledOnly)
    {
        await using var c = await db.OpenAsync();
        var sql = "SELECT COUNT(*) FROM users WHERE role = 'admin'" + (enabledOnly ? " AND enabled = 1" : "");
        return await c.ExecuteScalarAsync<long>(sql);
    }

    public async Task<long> InsertAsync(UserRow u)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>("""
            INSERT INTO users (username, display_name, password_hash, role, plan_id, enabled, expires_at, note, security_stamp, created_at)
            VALUES (@Username, @DisplayName, @PasswordHash, @Role, @PlanId, @Enabled, @ExpiresAt, @Note, @SecurityStamp, @CreatedAt)
            RETURNING id
            """, u);
    }

    public async Task UpdateAsync(UserRow u) => await UpdateAsync(u, keepAnAdmin: false);

    /// <summary>
    /// Saves the user. With keepAnAdmin the write only happens if another enabled admin exists, checked
    /// inside the same statement so two admins cannot remove each other at once. Returns false when refused.
    /// </summary>
    public async Task<bool> UpdateAsync(UserRow u, bool keepAnAdmin)
    {
        await using var c = await db.OpenAsync();
        var n = await c.ExecuteAsync("""
            UPDATE users SET display_name = @DisplayName, password_hash = @PasswordHash, role = @Role, plan_id = @PlanId,
                enabled = @Enabled, expires_at = @ExpiresAt, note = @Note, security_stamp = @SecurityStamp,
                quota_reset_at = @QuotaResetAt
            WHERE id = @Id
              AND (@Keep = 0 OR (SELECT COUNT(*) FROM users WHERE role = 'admin' AND enabled = 1 AND id <> @Id) > 0)
            """,
            new { u.Id, u.DisplayName, u.PasswordHash, u.Role, u.PlanId, u.Enabled, u.ExpiresAt, u.Note, u.SecurityStamp, u.QuotaResetAt, Keep = keepAnAdmin });
        return n > 0;
    }

    public async Task TouchLoginAsync(long id, long now)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("UPDATE users SET last_login_at = @now WHERE id = @id", new { id, now });
    }

    /// <summary>Deletes the user; with keepAnAdmin only if another enabled admin exists. Returns false when refused.</summary>
    public async Task<bool> DeleteAsync(long id, bool keepAnAdmin)
    {
        await using var c = await db.OpenAsync();
        var n = await c.ExecuteAsync("""
            DELETE FROM users WHERE id = @id
              AND (@keep = 0 OR (SELECT COUNT(*) FROM users WHERE role = 'admin' AND enabled = 1 AND id <> @id) > 0)
            """, new { id, keep = keepAnAdmin });
        return n > 0;
    }
}

public sealed class PlanStore(Database db)
{
    public async Task<List<PlanRow>> ListAsync()
    {
        await using var c = await db.OpenAsync();
        var plans = (await c.QueryAsync<PlanRow>(
            "SELECT id, name, max_concurrency, allowed_models, created_at, remote_enabled, remote_max_sessions, remote_memory_mb FROM plans ORDER BY name")).ToList();
        var limits = (await c.QueryAsync<PlanLimitRow>(
            "SELECT id, plan_id, window_hours, kind, amount FROM plan_limits ORDER BY window_hours, id")).ToList();
        foreach (var p in plans)
        {
            p.Limits = limits.Where(l => l.PlanId == p.Id).ToList();
        }
        return plans;
    }

    public async Task<PlanRow?> GetAsync(long id)
    {
        await using var c = await db.OpenAsync();
        var plan = await c.QuerySingleOrDefaultAsync<PlanRow>(
            "SELECT id, name, max_concurrency, allowed_models, created_at, remote_enabled, remote_max_sessions, remote_memory_mb FROM plans WHERE id = @id", new { id });
        if (plan is not null)
        {
            plan.Limits = (await c.QueryAsync<PlanLimitRow>(
                "SELECT id, plan_id, window_hours, kind, amount FROM plan_limits WHERE plan_id = @id ORDER BY window_hours, id",
                new { id })).ToList();
        }
        return plan;
    }

    public async Task<bool> NameTakenAsync(string name, long? exceptId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM plans WHERE name = @name AND id <> @id", new { name, id = exceptId ?? 0 }) > 0;
    }

    public async Task<long> SaveAsync(PlanRow plan)
    {
        await using var c = await db.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        long id;
        if (plan.Id == 0)
        {
            id = await c.ExecuteScalarAsync<long>("""
                INSERT INTO plans (name, max_concurrency, allowed_models, created_at, remote_enabled, remote_max_sessions, remote_memory_mb)
                VALUES (@Name, @MaxConcurrency, @AllowedModels, @CreatedAt, @RemoteEnabled, @RemoteMaxSessions, @RemoteMemoryMb) RETURNING id
                """, plan, tx);
        }
        else
        {
            id = plan.Id;
            await c.ExecuteAsync(
                "UPDATE plans SET name = @Name, max_concurrency = @MaxConcurrency, allowed_models = @AllowedModels, " +
                "remote_enabled = @RemoteEnabled, remote_max_sessions = @RemoteMaxSessions, remote_memory_mb = @RemoteMemoryMb WHERE id = @Id",
                plan, tx);
            await c.ExecuteAsync("DELETE FROM plan_limits WHERE plan_id = @id", new { id }, tx);
        }
        foreach (var l in plan.Limits)
        {
            await c.ExecuteAsync(
                "INSERT INTO plan_limits (plan_id, window_hours, kind, amount) VALUES (@planId, @WindowHours, @Kind, @Amount)",
                new { planId = id, l.WindowHours, l.Kind, l.Amount }, tx);
        }
        await tx.CommitAsync();
        return id;
    }

    public async Task<long> CountUsersAsync(long planId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM users WHERE plan_id = @planId", new { planId });
    }

    public async Task DeleteAsync(long id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("DELETE FROM plans WHERE id = @id", new { id });
    }
}

public sealed class KeyStore(Database db)
{
    private const string Columns = "id, user_id, name, prefix, hash, created_at, last_used_at, revoked_at";

    public async Task<long> InsertAsync(ApiKeyRow k)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>("""
            INSERT INTO api_keys (user_id, name, prefix, hash, created_at)
            VALUES (@UserId, @Name, @Prefix, @Hash, @CreatedAt) RETURNING id
            """, k);
    }

    public async Task<ApiKeyRow?> FindActiveByHashAsync(byte[] hash)
    {
        await using var c = await db.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<ApiKeyRow>(
            $"SELECT {Columns} FROM api_keys WHERE hash = @hash AND revoked_at IS NULL", new { hash });
    }

    public async Task<List<ApiKeyRow>> ListForUserAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<ApiKeyRow>(
            $"SELECT {Columns} FROM api_keys WHERE user_id = @userId ORDER BY created_at DESC", new { userId })).ToList();
    }

    public async Task<long> CountActiveAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM api_keys WHERE user_id = @userId AND revoked_at IS NULL", new { userId });
    }

    /// <summary>Revokes a key; when userId is given the key must belong to that user.</summary>
    public async Task<bool> RevokeAsync(long id, long? userId, long now)
    {
        await using var c = await db.OpenAsync();
        var n = await c.ExecuteAsync("""
            UPDATE api_keys SET revoked_at = @now
            WHERE id = @id AND revoked_at IS NULL AND (@userId IS NULL OR user_id = @userId)
            """, new { id, userId, now });
        return n > 0;
    }

    public async Task RevokeAllAsync(long userId, long now)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync(
            "UPDATE api_keys SET revoked_at = @now WHERE user_id = @userId AND revoked_at IS NULL", new { userId, now });
    }

    /// <summary>Records use at most once a minute per key so busy keys do not write on every request.</summary>
    public async Task TouchAsync(long id, long now)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("""
            UPDATE api_keys SET last_used_at = @now
            WHERE id = @id AND (last_used_at IS NULL OR last_used_at < @cutoff)
            """, new { id, now, cutoff = now - 60_000 });
    }
}

public sealed class PriceStore(Database db)
{
    public async Task<List<PriceRow>> ListAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<PriceRow>(
            "SELECT model, input_per_m, cached_per_m, output_per_m FROM prices ORDER BY model")).ToList();
    }

    public async Task ReplaceAllAsync(IEnumerable<PriceRow> prices)
    {
        await using var c = await db.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("DELETE FROM prices", transaction: tx);
        foreach (var p in prices)
        {
            await c.ExecuteAsync(
                "INSERT INTO prices (model, input_per_m, cached_per_m, output_per_m) VALUES (@Model, @InputPerM, @CachedPerM, @OutputPerM)",
                p, tx);
        }
        await tx.CommitAsync();
    }
}

public sealed class SettingsStore(Database db)
{
    public const string DefaultModel = "default_model";
    public const string PublicUrl = "public_url";
    public const string AllowWebSearch = "allow_web_search";
    public const string WebSearchPrice = "web_search_price";
    public const string InstanceSecret = "instance_secret";
    public const string OpenAiKey = "openai_key";
    public const string SetupDone = "setup_done";
    public const string Domain = "domain";
    public const string CertMethod = "cert_method";
    public const string CloudflareToken = "cloudflare_token";
    public const string AcmeEmail = "acme_email";
    public const double DefaultWebSearchPrice = 0.01;

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<(string Key, string Value)>("SELECT key, value FROM settings"))
            .ToDictionary(r => r.Key, r => r.Value);
    }

    public async Task SetAsync(string key, string? value)
    {
        await using var c = await db.OpenAsync();
        if (string.IsNullOrWhiteSpace(value))
        {
            await c.ExecuteAsync("DELETE FROM settings WHERE key = @key", new { key });
        }
        else
        {
            await c.ExecuteAsync(
                "INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { key, value = value.Trim() });
        }
    }
}
