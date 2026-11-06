using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace CodexGate.Tests;

public sealed class PanelTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Sign_in_works_and_wrong_passwords_do_not()
    {
        var client = _app.Browser();
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { username = "ghost", password = "nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/")).StatusCode);

        var admin = await _app.AdminAsync();
        var me = await GateFactory.JsonAsync(await admin.GetAsync("/api/me/"));
        Assert.Equal("admin", me.GetProperty("role").GetString());
        Assert.EndsWith("/v1", me.GetProperty("gateway").GetProperty("baseUrl").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/me/")).StatusCode);
    }

    [Fact]
    public async Task State_changing_calls_need_the_csrf_header()
    {
        var admin = await _app.AdminAsync();
        admin.DefaultRequestHeaders.Remove("X-CodexGate");
        var res = await admin.PostAsJsonAsync("/api/admin/plans", new { name = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("csrf_header_missing", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/plans")).StatusCode);
    }

    [Fact]
    public async Task Users_cannot_reach_admin_calls()
    {
        var admin = await _app.AdminAsync();
        await GateFactory.CreateUserAsync(admin, "sara", null);
        var sara = await _app.SignInAsync("sara", "user-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await sara.GetAsync("/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sara.PostAsJsonAsync("/api/admin/plans", new { name = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sara.GetAsync("/api/admin/backup")).StatusCode);
        var me = await GateFactory.JsonAsync(await sara.GetAsync("/api/me/"));
        Assert.Equal("no_plan", me.GetProperty("access").GetString());
    }

    [Fact]
    public async Task Disabling_a_user_or_changing_their_password_ends_their_sessions()
    {
        var admin = await _app.AdminAsync();
        var id = await GateFactory.CreateUserAsync(admin, "reza", null);
        var reza = await _app.SignInAsync("reza", "user-password-1");
        Assert.Equal(HttpStatusCode.OK, (await reza.GetAsync("/api/me/")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/admin/users/{id}", new { password = "brand-new-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reza.GetAsync("/api/me/")).StatusCode);

        reza = await _app.SignInAsync("reza", "brand-new-pass");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/admin/users/{id}", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await reza.GetAsync("/api/me/")).StatusCode);
        var again = await _app.Browser().PostAsJsonAsync("/api/auth/login", new { username = "reza", password = "brand-new-pass" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
    }

    [Fact]
    public async Task Users_change_their_own_password_and_stay_signed_in()
    {
        var admin = await _app.AdminAsync();
        await GateFactory.CreateUserAsync(admin, "mina", null);
        var mina = await _app.SignInAsync("mina", "user-password-1");
        var other = await _app.SignInAsync("mina", "user-password-1");
        Assert.Equal(HttpStatusCode.BadRequest, (await mina.PostAsJsonAsync("/api/me/password", new { current = "wrong-one", next = "another-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mina.PostAsJsonAsync("/api/me/password", new { current = "user-password-1", next = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await mina.PostAsJsonAsync("/api/me/password", new { current = "user-password-1", next = "another-pass" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mina.GetAsync("/api/me/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/me/")).StatusCode);
        await _app.SignInAsync("mina", "another-pass");
    }

    [Fact]
    public async Task The_last_admin_cannot_be_removed_or_demoted()
    {
        var admin = await _app.AdminAsync();
        var me = await GateFactory.JsonAsync(await admin.GetAsync("/api/me/"));
        var id = me.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/admin/users/{id}", new { role = "user" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync($"/api/admin/users/{id}", new { enabled = false })).StatusCode);

        var second = await GateFactory.CreateUserAsync(admin, "boss2", null, new { Role = "admin" });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/admin/users/{second}", new { role = "user" })).StatusCode);
    }

    [Fact]
    public async Task Validates_users_plans_prices_and_settings()
    {
        var admin = await _app.AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/users", new { username = "x", password = "long-enough" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/users", new { username = "valid", password = "short" })).StatusCode);
        await GateFactory.CreateUserAsync(admin, "taken", null);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/users", new { username = "TAKEN", password = "long-enough" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/plans", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/plans", new { name = "p", allowedModels = "gp*t" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/plans",
            new { name = "p", limits = new[] { new { windowHours = 0, kind = "tokens", amount = 1 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/plans",
            new { name = "p", limits = new[] { new { windowHours = 5, kind = "tokens", amount = 1 }, new { windowHours = 5, kind = "tokens", amount = 2 } } })).StatusCode);
        await GateFactory.CreatePlanAsync(admin, new { name = "Same" });
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/plans", new { name = "same" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/prices", new[] { new { model = "gpt", inputPerM = -1.0, cachedPerM = 0.0, outputPerM = 0.0 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings", new { publicUrl = "ftp://x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/settings", new { publicUrl = "https://codex.example.com/", defaultModel = "gpt-5.5" })).StatusCode);
        var me = await GateFactory.JsonAsync(await admin.GetAsync("/api/me/"));
        Assert.Equal("https://codex.example.com/v1", me.GetProperty("gateway").GetProperty("baseUrl").GetString());
    }

    [Fact]
    public async Task A_plan_in_use_cannot_be_deleted()
    {
        var admin = await _app.AdminAsync();
        var plan = await GateFactory.CreatePlanAsync(admin, new { name = "Busy" });
        var user = await GateFactory.CreateUserAsync(admin, "nima", plan);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/plans/{plan}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/users/{user}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/plans/{plan}")).StatusCode);
    }

    [Fact]
    public async Task Keys_belong_to_their_owner()
    {
        var admin = await _app.AdminAsync();
        await GateFactory.CreateUserAsync(admin, "user1", null);
        await GateFactory.CreateUserAsync(admin, "user2", null);
        var (_, panel1, _) = await _app.CodexForAsync("user1");
        var (_, panel2, _) = await _app.CodexForAsync("user2");
        var keyOf1 = (await GateFactory.JsonAsync(await panel1.GetAsync("/api/me/keys")))[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NotFound, (await panel2.DeleteAsync($"/api/me/keys/{keyOf1}")).StatusCode);
        var listed = await GateFactory.JsonAsync(await panel2.GetAsync("/api/me/keys"));
        Assert.Equal(1, listed.GetArrayLength());
        Assert.DoesNotContain("hash", listed.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exports_usage_as_csv_and_the_database_as_a_backup()
    {
        var admin = await _app.AdminAsync();
        var plan = await GateFactory.CreatePlanAsync(admin, new { name = "Open" });
        await GateFactory.CreateUserAsync(admin, "omid", plan);
        var (codex, _, _) = await _app.CodexForAsync("omid");
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);

        var csv = await admin.GetAsync("/api/admin/usage.csv?days=7");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var lines = (await csv.Content.ReadAsStringAsync()).Trim().Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("time_utc,username,model", lines[0]);
        Assert.Contains(",omid,gpt-5.5,responses,200,100,40,20,", lines[1]);

        var backup = await admin.GetAsync("/api/admin/backup");
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        var bytes = await backup.Content.ReadAsByteArrayAsync();
        Assert.Equal("SQLite format 3\0", Encoding.ASCII.GetString(bytes, 0, 16));
    }

    [Fact]
    public async Task Serves_the_panel_with_strict_browser_headers()
    {
        var client = _app.CreateClient();
        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("app.js", html);
        Assert.Contains("i18n.js", html);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("ok", await client.GetStringAsync("/healthz"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/nothing-here")).StatusCode);
    }
}

public sealed class FirstRunTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Without_a_configured_password_the_first_admin_gets_one_in_a_file()
    {
        _app.Settings["Gate:AdminPassword"] = "";
        _app.Settings["Gate:AdminUsername"] = "owner";
        _app.CreateClient();
        var file = Path.Combine(_app.DataDir, "initial-admin-password.txt");
        Assert.True(File.Exists(file));
        var lines = await File.ReadAllLinesAsync(file);
        Assert.Equal("username: owner", lines[0]);
        var password = lines[1]["password: ".Length..];
        await _app.SignInAsync("owner", password);
    }
}

/// <summary>A panel that does not even parse is a dead panel; no other test would notice (see v1.7.3 of IRNetFree).</summary>
public class PanelScriptTests
{
    [Theory]
    [InlineData("app.js")]
    [InlineData("i18n.js")]
    public void Panel_scripts_parse(string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/CodexGate/wwwroot", file));
        Assert.True(File.Exists(path), path);
        ProcessStartInfo start;
        try
        {
            start = new ProcessStartInfo("node", ["--check", path]) { RedirectStandardError = true, UseShellExecute = false };
            using var probe = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            probe!.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return; // node is not installed on this machine: nothing to check with
        }
        using var check = Process.Start(start)!;
        var errors = check.StandardError.ReadToEnd();
        check.WaitForExit();
        Assert.True(check.ExitCode == 0, errors);
    }

    [Fact]
    public void Both_languages_have_every_string()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/CodexGate/wwwroot/i18n.js"));
        var text = File.ReadAllText(path);
        var fa = Keys(text, "fa: {", "en: {");
        var en = Keys(text, "en: {", null);
        Assert.NotEmpty(fa);
        Assert.Equal(en.Order(), fa.Order());

        var app = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "app.js"));
        var used = System.Text.RegularExpressions.Regex.Matches(app, @"\bt\('([A-Za-z0-9_]+)'").Select(m => m.Groups[1].Value).Distinct();
        Assert.Empty(used.Except(en));
    }

    private static List<string> Keys(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = end is null ? text.Length : text.IndexOf(end, StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Matches(text[from..to], @"^\s{4}([A-Za-z0-9_]+):", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
    }
}
