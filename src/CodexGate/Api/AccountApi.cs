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
            Results.Ok((await keys.ListPersonalAsync(ctx.CurrentUser()!.Id)).Select(KeyView.From)));
        me.MapPost("/keys", CreateKeyAsync);
        me.MapDelete("/keys/{id:long}", async (long id, HttpContext ctx, KeyStore keys, PanelViews views) =>
            await keys.RevokeAsync(id, ctx.CurrentUser()!.Id, views.Now()) ? Results.NoContent() : Errors.NotFound());
        me.MapGet("/usage", async (HttpContext ctx, UsageStore usage, DeviceStore devices, PanelViews views, int? days, int? tz) =>
        {
            var user = ctx.CurrentUser()!;
            var (since, offset) = Range(views.Now(), days, tz);
            var names = (await devices.ListAsync(user.Id)).ToDictionary(d => d.Id, d => d.Name);
            return Results.Ok(new
            {
                daily = await usage.DailyAsync(user.Id, since, offset),
                recent = (await usage.RecentAsync(user.Id, 50))
                    .Select(u => UsageView.From(u, device: u.DeviceId is { } d ? names.GetValueOrDefault(d) : null)),
                byDevice = (await usage.ByDeviceAsync(user.Id, since)).Select(g => new
                {
                    g.DeviceId,
                    name = g.DeviceId is { } d ? names.GetValueOrDefault(d) : null,
                    g.Requests,
                    tokens = Math.Round(g.Tokens, 2),
                    costUsd = Math.Round(g.CostUsd, 6),
                    g.LastAt,
                }),
            });
        });

        me.MapGet("/devices", ListDevicesAsync);
        me.MapPost("/devices", (DeviceInput input, HttpContext ctx, DeviceStore devices, PanelViews views) =>
            SaveDeviceAsync(null, input, ctx, devices, views));
        me.MapPut("/devices/{id:long}", (long id, DeviceInput input, HttpContext ctx, DeviceStore devices, PanelViews views) =>
            SaveDeviceAsync(id, input, ctx, devices, views));
        me.MapDelete("/devices/{id:long}", DeleteDeviceAsync);
    }

    private static async Task<IResult> ListDevicesAsync(
        HttpContext ctx, DeviceStore devices, KeyStore keys, UsageStore usage, CodexGate.Gateway.QuotaService quota, PanelViews views)
    {
        var user = ctx.CurrentUser()!;
        var now = views.Now();
        var list = await devices.ListAsync(user.Id);
        var activeKeys = (await keys.ListForUserAsync(user.Id)).Where(k => k.RevokedAt is null && k.DeviceId is not null)
            .GroupBy(k => k.DeviceId!.Value).ToDictionary(g => g.Key, g => (long)g.Count());
        var used = (await usage.ByDeviceAsync(user.Id, now - 30 * 86_400_000L)).Where(u => u.DeviceId is not null)
            .ToDictionary(u => u.DeviceId!.Value);
        var result = new List<DeviceView>();
        foreach (var d in list)
        {
            result.Add(DeviceView.From(d, await quota.EvaluateDeviceAsync(d, now), activeKeys.GetValueOrDefault(d.Id), used.GetValueOrDefault(d.Id)));
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> SaveDeviceAsync(long? id, DeviceInput input, HttpContext ctx, DeviceStore devices, PanelViews views)
    {
        var user = ctx.CurrentUser()!;
        DeviceRow device;
        if (id is { } existing)
        {
            var found = await devices.GetAsync(existing);
            if (found is null || found.UserId != user.Id)
            {
                return Errors.NotFound();
            }
            device = found;
        }
        else
        {
            if (await devices.CountAsync(user.Id) >= DeviceStore.MaxPerUser)
            {
                return Errors.Json(400, "too_many_devices");
            }
            device = new DeviceRow { UserId = user.Id, CreatedAt = views.Now() };
        }
        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 40 || name.Any(char.IsControl))
        {
            return Errors.Json(400, "invalid_device_name");
        }
        var (error, limits) = AdminApi.CheckLimits(input.Limits);
        if (error is not null)
        {
            return Errors.Json(400, error);
        }
        device.Name = name;
        device.Limits = limits;
        var saved = await devices.SaveAsync(device);
        return saved is null ? Errors.Json(409, "device_name_taken") : Results.Ok(new { id = saved });
    }

    /// <summary>Removes a device and revokes its keys; refused while a server session still counts against it.</summary>
    private static async Task<IResult> DeleteDeviceAsync(
        long id, HttpContext ctx, DeviceStore devices, KeyStore keys, RemoteStore sessions, PanelViews views)
    {
        var user = ctx.CurrentUser()!;
        var device = await devices.GetAsync(id);
        if (device is null || device.UserId != user.Id)
        {
            return Errors.NotFound();
        }
        if (await sessions.CountForDeviceAsync(id) > 0)
        {
            return Errors.Json(409, "device_in_use");
        }
        await keys.RevokeForDeviceAsync(id, views.Now());
        await devices.DeleteAsync(id);
        return Results.NoContent();
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

    private static async Task<IResult> CreateKeyAsync(NewKeyRequest req, HttpContext ctx, KeyStore keys, DeviceStore devices, PanelViews views)
    {
        var user = ctx.CurrentUser()!;
        if (await keys.CountPersonalActiveAsync(user.Id) >= MaxKeysPerUser)
        {
            return Errors.Json(400, "too_many_keys");
        }
        var name = (req.Name ?? "").Trim();
        if (name.Length > 40)
        {
            return Errors.Json(400, "name_too_long");
        }
        if (req.DeviceId is { } deviceId && (await devices.GetAsync(deviceId))?.UserId != user.Id)
        {
            return Errors.Json(400, "unknown_device");
        }
        var secret = ApiKeys.Generate();
        var row = new ApiKeyRow
        {
            UserId = user.Id,
            Name = name,
            Prefix = ApiKeys.DisplayPrefix(secret),
            Hash = ApiKeys.HashOf(secret),
            CreatedAt = views.Now(),
            DeviceId = req.DeviceId,
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
