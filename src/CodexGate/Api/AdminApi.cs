using System.Globalization;
using System.Text;
using CodexGate.Data;
using CodexGate.Gateway;
using CodexGate.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CodexGate.Api;

/// <summary>Everything only an admin may do: accounts, plans, prices, settings and the overview.</summary>
public static class AdminApi
{
    public const int MaxLimitsPerPlan = 8;

    public static void MapAdminApi(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization("admin");

        admin.MapGet("/users", ListUsersAsync);
        admin.MapPost("/users", CreateUserAsync);
        admin.MapPatch("/users/{id:long}", UpdateUserAsync);
        admin.MapPost("/users/{id:long}/renew", RenewAsync);
        admin.MapDelete("/users/{id:long}", DeleteUserAsync);
        admin.MapPost("/users/{id:long}/reset-usage", async (long id, UserStore users, PanelViews views) =>
        {
            var user = await users.GetAsync(id);
            if (user is null)
            {
                return Errors.NotFound();
            }
            user.QuotaResetAt = views.Now();
            await users.UpdateAsync(user);
            return Results.NoContent();
        });
        admin.MapGet("/users/{id:long}/keys", async (long id, KeyStore keys) =>
            Results.Ok((await keys.ListForUserAsync(id)).Select(KeyView.From)));
        admin.MapDelete("/keys/{id:long}", async (long id, KeyStore keys, PanelViews views) =>
            await keys.RevokeAsync(id, null, views.Now()) ? Results.NoContent() : Errors.NotFound());

        admin.MapGet("/plans", async (PlanStore plans) =>
        {
            var list = await plans.ListAsync();
            var views = new List<PlanView>();
            foreach (var p in list)
            {
                views.Add(PlanView.From(p, await plans.CountUsersAsync(p.Id)));
            }
            return Results.Ok(views);
        });
        admin.MapPost("/plans", (PlanInput input, PlanStore plans, PanelViews views) => SavePlanAsync(null, input, plans, views));
        admin.MapPut("/plans/{id:long}", (long id, PlanInput input, PlanStore plans, PanelViews views) => SavePlanAsync(id, input, plans, views));
        admin.MapDelete("/plans/{id:long}", async (long id, PlanStore plans) =>
        {
            if (await plans.GetAsync(id) is null)
            {
                return Errors.NotFound();
            }
            if (await plans.CountUsersAsync(id) > 0)
            {
                return Errors.Json(409, "plan_in_use");
            }
            await plans.DeleteAsync(id);
            return Results.NoContent();
        });

        admin.MapGet("/prices", async (PriceStore prices) => Results.Ok(await prices.ListAsync()));
        admin.MapPut("/prices", SavePricesAsync);

        admin.MapGet("/settings", async (SettingsStore settings, HttpContext ctx) =>
        {
            var all = await settings.GetAllAsync();
            return Results.Ok(new
            {
                defaultModel = all.GetValueOrDefault(SettingsStore.DefaultModel) ?? "",
                publicUrl = all.GetValueOrDefault(SettingsStore.PublicUrl) ?? "",
                allowWebSearch = all.GetValueOrDefault(SettingsStore.AllowWebSearch) == "1",
                webSearchPrice = double.TryParse(all.GetValueOrDefault(SettingsStore.WebSearchPrice), NumberStyles.Float, CultureInfo.InvariantCulture, out var wsp)
                    ? wsp : SettingsStore.DefaultWebSearchPrice,
                effectiveBaseUrl = PanelViews.BaseUrl(all.GetValueOrDefault(SettingsStore.PublicUrl), ctx.Request),
            });
        });
        admin.MapPut("/settings", SaveSettingsAsync);

        admin.MapGet("/overview", OverviewAsync);
        admin.MapGet("/usage.csv", ExportCsvAsync);
        admin.MapGet("/backup", BackupAsync);
        admin.MapGet("/usage", async (UsageStore usage, UserStore users, long? userId, int? limit) =>
        {
            var names = (await users.ListAsync()).ToDictionary(u => u.Id, u => u.Username);
            var rows = await usage.RecentAsync(userId, Math.Clamp(limit ?? 100, 1, 500));
            return Results.Ok(rows.Select(r => UsageView.From(r, names.GetValueOrDefault(r.UserId))));
        });
    }

    private static async Task<IResult> ListUsersAsync(
        UserStore users, PlanStore plans, KeyStore keys, UsageStore usage, QuotaService quota, PanelViews views)
    {
        var now = views.Now();
        var planList = (await plans.ListAsync()).ToDictionary(p => p.Id);
        var lastUsed = await usage.LastUsedByUserAsync();
        var result = new List<AdminUserView>();
        foreach (var u in await users.ListAsync())
        {
            var plan = u.PlanId is { } pid ? planList.GetValueOrDefault(pid) : null;
            var windows = plan is null ? [] : await quota.EvaluateAsync(u, plan.Limits, now);
            result.Add(new AdminUserView(
                u.Id, u.Username, u.DisplayName, u.Role, u.Enabled, u.PlanId, plan?.Name, u.ExpiresAt,
                Access.Code(Access.Check(u, plan, now)), u.Note, u.CreatedAt, u.LastLoginAt,
                lastUsed.TryGetValue(u.Id, out var at) ? at : null, await keys.CountActiveAsync(u.Id),
                windows.Select(WindowView.From).ToList()));
        }
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateUserAsync(UserInput input, UserStore users, PlanStore plans, PanelViews views)
    {
        var username = Validation.Username(input.Username);
        if (username is null)
        {
            return Errors.Json(400, "invalid_username");
        }
        if (await users.GetByUsernameAsync(username) is not null)
        {
            return Errors.Json(409, "username_taken");
        }
        if (!Validation.Password(input.Password))
        {
            return Errors.Json(400, "weak_password");
        }
        var role = input.Role ?? Roles.User;
        if (!Roles.IsValid(role))
        {
            return Errors.Json(400, "invalid_role");
        }
        if (input.PlanId is { } planId && await plans.GetAsync(planId) is null)
        {
            return Errors.Json(400, "unknown_plan");
        }
        var now = views.Now();
        var (expiryOk, expiresAt) = Expiry(input, null, now);
        if (!expiryOk)
        {
            return Errors.Json(400, "invalid_expiry");
        }
        var note = (input.Note ?? "").Trim();
        var displayName = (input.DisplayName ?? "").Trim();
        if (note.Length > 500 || displayName.Length > 64)
        {
            return Errors.Json(400, "text_too_long");
        }
        var row = new UserRow
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = PasswordHasher.Hash(input.Password!),
            Role = role,
            PlanId = input.PlanId,
            Enabled = input.Enabled ?? true,
            ExpiresAt = expiresAt,
            Note = note,
            SecurityStamp = ApiKeys.NewStamp(),
            CreatedAt = now,
        };
        row.Id = await users.InsertAsync(row);
        return Results.Ok(new { id = row.Id });
    }

    private static async Task<IResult> UpdateUserAsync(
        long id, UserInput input, HttpContext ctx, UserStore users, PlanStore plans, KeyStore keys, PanelViews views)
    {
        var user = await users.GetAsync(id);
        if (user is null)
        {
            return Errors.NotFound();
        }
        var me = ctx.CurrentUser()!;
        var now = views.Now();
        var rotate = false;
        var wasActiveAdmin = user.IsAdmin && user.Enabled;

        if (input.Role is { } role && role != user.Role)
        {
            if (!Roles.IsValid(role))
            {
                return Errors.Json(400, "invalid_role");
            }
            user.Role = role;
            rotate = true;
        }
        if (input.Enabled is { } enabled && enabled != user.Enabled)
        {
            if (!enabled && user.Id == me.Id)
            {
                return Errors.Json(409, "cannot_disable_self");
            }
            user.Enabled = enabled;
            rotate = true;
        }
        if (input.Password is not null)
        {
            if (!Validation.Password(input.Password))
            {
                return Errors.Json(400, "weak_password");
            }
            user.PasswordHash = PasswordHasher.Hash(input.Password);
            rotate = true;
        }
        if (input.PlanId is { } planId)
        {
            if (planId <= 0)
            {
                user.PlanId = null;
            }
            else if (await plans.GetAsync(planId) is null)
            {
                return Errors.Json(400, "unknown_plan");
            }
            else
            {
                user.PlanId = planId;
            }
        }
        if (input.DisplayName is not null)
        {
            var displayName = input.DisplayName.Trim();
            if (displayName.Length > 64)
            {
                return Errors.Json(400, "text_too_long");
            }
            user.DisplayName = displayName;
        }
        if (input.Note is not null)
        {
            var note = input.Note.Trim();
            if (note.Length > 500)
            {
                return Errors.Json(400, "text_too_long");
            }
            user.Note = note;
        }
        if (input.NoExpiry is not null || input.ExpiresAt is not null || input.ExpiresInDays is not null)
        {
            var (ok, expiresAt) = Expiry(input, user.ExpiresAt, now);
            if (!ok)
            {
                return Errors.Json(400, "invalid_expiry");
            }
            user.ExpiresAt = expiresAt;
        }

        if (rotate)
        {
            user.SecurityStamp = ApiKeys.NewStamp();
        }
        var losesAnAdmin = wasActiveAdmin && !(user.IsAdmin && user.Enabled);
        if (!await users.UpdateAsync(user, keepAnAdmin: losesAnAdmin))
        {
            return Errors.Json(409, "last_admin");
        }
        if (user.Id == me.Id && rotate)
        {
            // The admin changed their own password or role: keep this browser signed in.
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                Claims.For(user));
        }
        return Results.NoContent();
    }

    private static async Task<IResult> RenewAsync(long id, RenewRequest req, UserStore users, PanelViews views)
    {
        var user = await users.GetAsync(id);
        if (user is null)
        {
            return Errors.NotFound();
        }
        if (req.Days is < 1 or > 3660)
        {
            return Errors.Json(400, "invalid_expiry");
        }
        var now = views.Now();
        var from = Math.Max(now, user.ExpiresAt ?? now);
        user.ExpiresAt = from + req.Days * 86_400_000L;
        await users.UpdateAsync(user);
        return Results.Ok(new { expiresAt = user.ExpiresAt });
    }

    private static async Task<IResult> DeleteUserAsync(
        long id, HttpContext ctx, UserStore users, CodexGate.Remote.RemoteService remote, RemoteStore remoteStore)
    {
        var user = await users.GetAsync(id);
        if (user is null)
        {
            return Errors.NotFound();
        }
        if (user.Id == ctx.CurrentUser()!.Id)
        {
            return Errors.Json(409, "cannot_delete_self");
        }
        var sessions = await remoteStore.ListAsync(id);
        if (!await users.DeleteAsync(id, keepAnAdmin: user.IsAdmin && user.Enabled))
        {
            return Errors.Json(409, "last_admin");
        }
        if (remote.Enabled)
        {
            foreach (var s in sessions)
            {
                try
                {
                    await remote.Host.KillAsync(id, s.TmuxName, CancellationToken.None);
                }
                catch (CodexGate.Remote.RemoteHostException)
                {
                }
            }
        }
        return Results.NoContent();
    }

    /// <summary>NoExpiry clears it; ExpiresInDays counts from now; ExpiresAt sets it outright.</summary>
    private static (bool Ok, long? ExpiresAt) Expiry(UserInput input, long? current, long now)
    {
        if (input.NoExpiry == true)
        {
            return (true, null);
        }
        if (input.ExpiresInDays is { } days)
        {
            return days is >= 1 and <= 3660 ? (true, now + days * 86_400_000L) : (false, null);
        }
        if (input.ExpiresAt is { } at)
        {
            return at > 0 ? (true, at) : (false, null);
        }
        return (true, current);
    }

    private static async Task<IResult> SavePlanAsync(long? id, PlanInput input, PlanStore plans, PanelViews views)
    {
        PlanRow plan;
        if (id is { } existing)
        {
            var found = await plans.GetAsync(existing);
            if (found is null)
            {
                return Errors.NotFound();
            }
            plan = found;
        }
        else
        {
            plan = new PlanRow { CreatedAt = views.Now() };
        }

        var name = (input.Name ?? "").Trim();
        if (name.Length is 0 or > 64)
        {
            return Errors.Json(400, "invalid_name");
        }
        if (await plans.NameTakenAsync(name, id))
        {
            return Errors.Json(409, "name_taken");
        }
        var concurrency = input.MaxConcurrency ?? 2;
        if (concurrency is < 1 or > 64)
        {
            return Errors.Json(400, "invalid_concurrency");
        }
        var models = ModelPatterns.Parse(input.AllowedModels);
        if (models.Count > 50 || models.Any(m => !ModelPatterns.IsValid(m)))
        {
            return Errors.Json(400, "invalid_models");
        }
        var limits = input.Limits ?? [];
        if (limits.Count > MaxLimitsPerPlan)
        {
            return Errors.Json(400, "too_many_limits");
        }
        if (limits.Any(l => l.WindowHours is < 1 or > 8784 || !LimitKinds.IsValid(l.Kind) || !double.IsFinite(l.Amount)
                || l.Amount < 0 || l.Amount > 1e13))
        {
            return Errors.Json(400, "invalid_limit");
        }
        if (limits.GroupBy(l => (l.WindowHours, l.Kind)).Any(g => g.Count() > 1))
        {
            return Errors.Json(400, "duplicate_limit");
        }

        var remoteSessions = input.RemoteMaxSessions ?? plan.RemoteMaxSessions;
        var remoteMemory = input.RemoteMemoryMb ?? plan.RemoteMemoryMb;
        if (remoteSessions is < 1 or > 20 || remoteMemory is < 0 or > 262_144)
        {
            return Errors.Json(400, "invalid_remote");
        }
        plan.RemoteEnabled = input.RemoteEnabled ?? plan.RemoteEnabled;
        plan.RemoteMaxSessions = remoteSessions;
        plan.RemoteMemoryMb = remoteMemory;
        plan.Name = name;
        plan.MaxConcurrency = concurrency;
        plan.AllowedModels = ModelPatterns.Join(models);
        plan.Limits = limits.Select(l => new PlanLimitRow { WindowHours = l.WindowHours, Kind = l.Kind!, Amount = l.Amount }).ToList();
        var savedId = await plans.SaveAsync(plan);
        return Results.Ok(new { id = savedId });
    }

    private static async Task<IResult> SavePricesAsync(List<PriceInput> input, PriceStore prices)
    {
        if (input.Count > 200)
        {
            return Errors.Json(400, "too_many_prices");
        }
        var rows = new List<PriceRow>();
        foreach (var p in input)
        {
            var model = (p.Model ?? "").Trim();
            double[] values = [p.InputPerM, p.CachedPerM, p.OutputPerM];
            if (!ModelPatterns.IsValid(model) || values.Any(v => !double.IsFinite(v) || v < 0 || v > 100_000))
            {
                return Errors.Json(400, "invalid_price", model);
            }
            rows.Add(new PriceRow { Model = model, InputPerM = p.InputPerM, CachedPerM = p.CachedPerM, OutputPerM = p.OutputPerM });
        }
        if (rows.GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            return Errors.Json(400, "duplicate_price");
        }
        await prices.ReplaceAllAsync(rows);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveSettingsAsync(SettingsInput input, SettingsStore settings)
    {
        var model = (input.DefaultModel ?? "").Trim();
        if (model.Length > 0 && (!ModelPatterns.IsValid(model) || model.Contains('*')))
        {
            return Errors.Json(400, "invalid_model");
        }
        var url = (input.PublicUrl ?? "").Trim().TrimEnd('/');
        if (url.Length > 0)
        {
            // The address ends up inside users' setup commands, so it must be a plain http(s) URL.
            if (url.Length > 200 || url.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '\\' or '<' or '>' or '`' or '$')
                || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return Errors.Json(400, "invalid_url");
            }
            url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        if (input.WebSearchPrice is { } price && (!double.IsFinite(price) || price < 0 || price > 10))
        {
            return Errors.Json(400, "invalid_price");
        }
        await settings.SetAsync(SettingsStore.DefaultModel, model);
        await settings.SetAsync(SettingsStore.PublicUrl, url);
        if (input.AllowWebSearch is { } allow)
        {
            await settings.SetAsync(SettingsStore.AllowWebSearch, allow ? "1" : "0");
        }
        if (input.WebSearchPrice is { } p)
        {
            await settings.SetAsync(SettingsStore.WebSearchPrice, p.ToString("R", CultureInfo.InvariantCulture));
        }
        return Results.NoContent();
    }

    private static async Task ExportCsvAsync(HttpContext ctx, UsageStore usage, UserStore users, PanelViews views, int? days)
    {
        var d = Math.Clamp(days ?? 30, 1, 366);
        var names = (await users.ListAsync()).ToDictionary(u => u.Id, u => u.Username);
        var rows = await usage.SinceAsync(views.Now() - d * 86_400_000L);
        var csv = new StringBuilder();
        csv.AppendLine("time_utc,username,model,endpoint,status,input_tokens,cached_tokens,output_tokens,reasoning_tokens,billable_tokens,cost_usd,estimated,duration_ms,upstream");
        foreach (var r in rows)
        {
            string[] cells =
            [
                DateTimeOffset.FromUnixTimeMilliseconds(r.At).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                names.GetValueOrDefault(r.UserId) ?? "#" + r.UserId.ToString(CultureInfo.InvariantCulture),
                r.Model, r.Endpoint, Num(r.Status), Num(r.InputTokens), Num(r.CachedTokens), Num(r.OutputTokens),
                Num(r.ReasoningTokens), r.BillableTokens.ToString("0.##", CultureInfo.InvariantCulture),
                r.CostUsd?.ToString("0.######", CultureInfo.InvariantCulture) ?? "", r.Estimated ? "1" : "0",
                Num(r.DurationMs), r.Upstream,
            ];
            csv.AppendLine(string.Join(',', cells.Select(CsvCell)));
        }
        ctx.Response.ContentType = "text/csv; charset=utf-8";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"codexgate-usage-{d}d.csv\"";
        await ctx.Response.WriteAsync(csv.ToString());
    }

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Quotes a CSV cell and defuses spreadsheet formulas (=, +, -, @ at the start).</summary>
    public static string CsvCell(string value)
    {
        var v = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>A consistent copy of the whole database (VACUUM INTO), streamed as a download.</summary>
    private static async Task BackupAsync(HttpContext ctx, Database db, TimeProvider time)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"codexgate-backup-{Guid.NewGuid():N}.db");
        try
        {
            await using (var c = await db.OpenAsync())
            {
                await Dapper.SqlMapper.ExecuteAsync(c, "VACUUM INTO @tmp", new { tmp });
            }
            var name = $"codexgate-{time.GetUtcNow().UtcDateTime:yyyyMMdd-HHmm}.db";
            ctx.Response.ContentType = "application/vnd.sqlite3";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            await ctx.Response.SendFileAsync(tmp);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<IResult> OverviewAsync(
        UsageStore usage, UserStore users, UpstreamPool pool, PanelViews views, int? tz)
    {
        var now = views.Now();
        var (since30, offset) = AccountApi.Range(now, 30, tz);
        var names = (await users.ListAsync()).ToDictionary(u => u.Id.ToString(CultureInfo.InvariantCulture), u => u.Username);
        var byUser = await usage.ByUserAsync(since30);
        foreach (var g in byUser)
        {
            g.Name = names.GetValueOrDefault(g.Name) ?? "#" + g.Name;
        }
        return Results.Ok(new
        {
            day = await usage.TotalsAsync(now - 86_400_000L),
            week = await usage.TotalsAsync(now - 7 * 86_400_000L),
            month = await usage.TotalsAsync(since30),
            daily = await usage.DailyAsync(null, since30, offset),
            byUser,
            byModel = await usage.ByModelAsync(since30),
            upstreams = pool.All.Select(u => new
            {
                u.Name,
                host = u.BaseUri.Host,
                models = u.Models,
                u.InFlight,
                u.Requests,
                u.LastError,
                u.LastErrorAt,
            }),
        });
    }
}
