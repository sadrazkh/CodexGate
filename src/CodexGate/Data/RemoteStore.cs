using Dapper;

namespace CodexGate.Data;

/// <summary>Server sessions a user started, and the gateway key their server account uses.</summary>
public sealed class RemoteStore(Database db)
{
    private const string Columns = "id, user_id, name, dir, program, created_at";

    public async Task<long> InsertAsync(RemoteSessionRow s)
    {
        await using var c = await db.OpenAsync();
        return await c.ExecuteScalarAsync<long>(
            "INSERT INTO remote_sessions (user_id, name, dir, program, created_at) VALUES (@UserId, @Name, @Dir, @Program, @CreatedAt) RETURNING id",
            s);
    }

    public async Task<RemoteSessionRow?> GetAsync(long id)
    {
        await using var c = await db.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<RemoteSessionRow>($"SELECT {Columns} FROM remote_sessions WHERE id = @id", new { id });
    }

    public async Task<List<RemoteSessionRow>> ListAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return (await c.QueryAsync<RemoteSessionRow>(
            $"SELECT {Columns} FROM remote_sessions WHERE user_id = @userId ORDER BY id DESC", new { userId })).ToList();
    }

    public async Task DeleteAsync(long id)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("DELETE FROM remote_sessions WHERE id = @id", new { id });
    }

    public async Task<long?> AccountKeyAsync(long userId)
    {
        await using var c = await db.OpenAsync();
        return await c.QuerySingleOrDefaultAsync<long?>("SELECT key_id FROM remote_accounts WHERE user_id = @userId", new { userId });
    }

    public async Task SetAccountAsync(long userId, long keyId, long now)
    {
        await using var c = await db.OpenAsync();
        await c.ExecuteAsync("""
            INSERT INTO remote_accounts (user_id, key_id, provisioned_at) VALUES (@userId, @keyId, @now)
            ON CONFLICT(user_id) DO UPDATE SET key_id = excluded.key_id, provisioned_at = excluded.provisioned_at
            """, new { userId, keyId, now });
    }
}
