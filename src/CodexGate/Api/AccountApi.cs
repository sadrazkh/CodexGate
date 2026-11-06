using CodexGate.Data;
using CodexGate.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CodexGate.Api;

/// <summary>Sign-in and the signed-in user's own page: plan, quota, keys, usage, password.</summary>
public static class AccountApi
{
    public const int MaxKeysPerUser = 10;

    public static void MapAccountApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", LoginAsync).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        var me = app.MapGroup("/api/me").RequireAuthorization();
        me.MapGet("/", async (HttpContext ctx, PanelViews views) => Results.Ok(await views.MeAsync(ctx.CurrentUser()!, ctx.Request)));
        me.MapPost("/password", ChangePasswordAsync).RequireRateLimiting("login");
        me.MapGet("/keys", async (HttpContext ctx, KeyStore keys) =>
            Results.Ok((await keys.ListForUserAsync(ctx.CurrentUser()!.Id)).Select(KeyView.From)));
        me.MapPost("/keys", CreateKeyAsync);
        me.MapDelete("/keys/{id:long}", async (long id, HttpContext ctx, KeyStore keys, PanelViews views) =>
            await keys.RevokeAsync(id, ctx.CurrentUser()!.Id, views.Now()) ? Results.NoContent() : Errors.NotFound());
        me.MapGet("/usage", async (HttpContext ctx, UsageStore usage, PanelViews views, int? days, int? tz) =>
        {
            var user = ctx.CurrentUser()!;
            var (since, offset) = Range(views.Now(), days, tz);
            return Results.Ok(new
            {
                daily = await usage.DailyAsync(user.Id, since, offset),
                recent = (await usage.RecentAsync(user.Id, 50)).Select(u => UsageView.From(u)),
            });
        });
    }

    private static async Task<IResult> LoginAsync(LoginRequest req, HttpContext ctx, UserStore users, PanelViews views, LoginGuard guard)
    {
        var username = req.Username?.Trim() ?? "";
        var password = req.Password ?? "";
        var user = username.Length is > 0 and <= 64 ? await users.GetByUsernameAsync(username) : null;
        if (user is not null && guard.IsLocked(user.Id))
        {
            return Errors.Json(429, "too_many_attempts");
        }
        var ok = PasswordHasher.Verify(password, user?.PasswordHash ?? PasswordHasher.DummyHash) && user is not null;
        if (!ok)
        {
            if (user is not null)
            {
                guard.Failed(user.Id);
            }
            return Errors.Json(401, "invalid_credentials");
        }
        guard.Succeeded(user!.Id);
        if (!user.Enabled)
        {
            return Errors.Json(403, "account_disabled");
        }
        if (PasswordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(password);
            await users.UpdateAsync(user);
        }
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Claims.For(user));
        await users.TouchLoginAsync(user.Id, views.Now());
        return Results.Ok(await views.MeAsync(user, ctx.Request));
    }

    private static async Task<IResult> ChangePasswordAsync(ChangePasswordRequest req, HttpContext ctx, UserStore users)
    {
        var user = ctx.CurrentUser()!;
        if (!PasswordHasher.Verify(req.Current ?? "", user.PasswordHash))
        {
            return Errors.Json(400, "wrong_password");
        }
        if (!Validation.Password(req.Next))
        {
            return Errors.Json(400, "weak_password");
        }
        user.PasswordHash = PasswordHasher.Hash(req.Next!);
        user.SecurityStamp = ApiKeys.NewStamp();
        await users.UpdateAsync(user);
        // Keep this browser signed in under the new stamp; every other session ends.
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Claims.For(user));
        return Results.NoContent();
    }

    private static async Task<IResult> CreateKeyAsync(NewKeyRequest req, HttpContext ctx, KeyStore keys, PanelViews views)
    {
        var user = ctx.CurrentUser()!;
        if (await keys.CountActiveAsync(user.Id) >= MaxKeysPerUser)
        {
            return Errors.Json(400, "too_many_keys");
        }
        var name = (req.Name ?? "").Trim();
        if (name.Length > 40)
        {
            return Errors.Json(400, "name_too_long");
        }
        var secret = ApiKeys.Generate();
        var row = new ApiKeyRow
        {
            UserId = user.Id,
            Name = name,
            Prefix = ApiKeys.DisplayPrefix(secret),
            Hash = ApiKeys.HashOf(secret),
            CreatedAt = views.Now(),
        };
        row.Id = await keys.InsertAsync(row);
        return Results.Ok(new { key = secret, info = KeyView.From(row) });
    }

    public static (long Since, long OffsetMs) Range(long now, int? days, int? tzMinutes)
    {
        var d = Math.Clamp(days ?? 30, 1, 90);
        var tz = Math.Clamp(tzMinutes ?? 0, -14 * 60, 14 * 60);
        return (now - d * 86_400_000L, tz * 60_000L);
    }
}

public static class Errors
{
    public static IResult Json(int status, string code, string? detail = null) =>
        Results.Json(new { error = code, detail }, statusCode: status);

    public static IResult NotFound() => Json(404, "not_found");
}
