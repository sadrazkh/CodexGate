using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CodexGate.Data;
using CodexGate.Gateway;
using CodexGate.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CodexGate.Tests;

public sealed class FakeIssuer : ICertificateIssuer
{
    public List<IssueRequest> Requests { get; } = [];
    public Exception? Fail { get; set; }
    public int Days { get; set; } = 90;

    public Task<IssuedCertificate> IssueAsync(IssueRequest request, Action<string> progress, CancellationToken ct)
    {
        Requests.Add(request);
        progress("account");
        progress("order");
        progress("validate");
        if (Fail is not null)
        {
            throw Fail;
        }
        progress("certificate");
        return Task.FromResult(SelfSigned(request.Domain, Days));
    }

    public static IssuedCertificate SelfSigned(string domain, int days)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + domain, key, HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));
        return new IssuedCertificate(cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }
}

public sealed class FakeRestarter : IAppRestarter
{
    public int Count { get; private set; }

    public void Restart() => Count++;
}

public sealed class SetupTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly GateFactory _app = new();
    private readonly FakeIssuer _issuer = new();
    private readonly FakeRestarter _restarter = new();

    public SetupTests()
    {
        _app.Settings["Gate:AdminPassword"] = "";
        _app.Settings["Gate:Managed:Enabled"] = "true";
        _app.Settings["Gate:Managed:SetupToken"] = Token;
        _app.Settings["Gate:Managed:PublicHttpsPort"] = "9443";
        _app.Settings["Gate:Managed:HttpChallengeAvailable"] = "true";
        _app.Settings["Gate:Managed:PublicIp"] = "203.0.113.7";
        _app.ExtraServices = s =>
        {
            s.AddSingleton<ICertificateIssuer>(_issuer);
            s.AddSingleton<IAppRestarter>(_restarter);
        };
    }

    public void Dispose() => _app.Dispose();

    private HttpClient Page(string? token = Token)
    {
        var c = _app.CreateClient();
        c.DefaultRequestHeaders.Add("X-CodexGate", "1");
        if (token is not null)
        {
            c.DefaultRequestHeaders.Add(SetupApi.TokenHeader, token);
        }
        return c;
    }

    private static object Input(string domain = "codex.example.test", string method = "http", string? key = "sk-test-1234") => new
    {
        username = "boss", password = "install-pass-1", domain, method, cloudflareToken = "", email = "", openAiKey = key,
    };

    private static async Task<JsonElement> WaitAsync(HttpClient page)
    {
        for (var i = 0; i < 100; i++)
        {
            var p = await GateFactory.JsonAsync(await page.GetAsync("/api/setup/progress"));
            if (!p.GetProperty("running").GetBoolean())
            {
                return p;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("setup did not finish");
    }

    [Fact]
    public async Task The_page_is_closed_without_the_token()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Page(null).GetAsync("/api/setup/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Page("wrong-token-wrong-token-wrong").GetAsync("/api/setup/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Page(null).PostAsJsonAsync("/api/setup/complete", Input())).StatusCode);
        var state = await GateFactory.JsonAsync(await Page().GetAsync("/api/setup/state"));
        Assert.Equal(9443, state.GetProperty("httpsPort").GetInt32());
        Assert.True(state.GetProperty("httpAvailable").GetBoolean());
        Assert.Equal("203.0.113.7", state.GetProperty("publicIp").GetString());
    }

    [Fact]
    public async Task No_admin_exists_until_setup_makes_one()
    {
        var res = await _app.Browser().PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "anything-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("not a domain", "http", "invalid_domain")]
    [InlineData("localhost", "http", "invalid_domain")]
    [InlineData("codex.example.test", "ftp", "invalid_method")]
    [InlineData("codex.example.test", "cloudflare", "invalid_cloudflare_token")]
    public async Task Refuses_bad_input(string domain, string method, string code)
    {
        var res = await Page().PostAsJsonAsync("/api/setup/complete", Input(domain, method));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(code, await res.Content.ReadAsStringAsync());
        Assert.Empty(_issuer.Requests);
    }

    [Fact]
    public async Task Setup_gets_the_certificate_then_saves_everything_restarts_and_closes()
    {
        var page = Page();
        Assert.Equal(HttpStatusCode.Accepted, (await page.PostAsJsonAsync("/api/setup/complete", Input())).StatusCode);
        var done = await WaitAsync(page);
        Assert.Equal("https://codex.example.test:9443", done.GetProperty("url").GetString());
        Assert.Contains("done", done.GetProperty("steps").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("codex.example.test", _issuer.Requests.Single().Domain);

        var certs = _app.Services.GetRequiredService<CertificateStore>();
        Assert.Equal("CN=codex.example.test", certs.Current!.Subject);
        Assert.True(File.Exists(certs.KeyPath));

        for (var i = 0; i < 100 && _restarter.Count == 0; i++)
        {
            await Task.Delay(50);
        }
        Assert.Equal(1, _restarter.Count);

        var admin = await _app.SignInAsync("boss", "install-pass-1");
        var settings = await GateFactory.JsonAsync(await admin.GetAsync("/api/admin/settings"));
        Assert.Equal("https://codex.example.test:9443", settings.GetProperty("publicUrl").GetString());
        Assert.Equal("…1234", settings.GetProperty("openAiKey").GetString());
        Assert.Equal("codex.example.test", settings.GetProperty("certificate").GetProperty("domain").GetString());
        Assert.Contains(_app.Services.GetRequiredService<UpstreamPool>().All, u => u.Name == UpstreamPool.SavedKeyName);

        Assert.Equal(HttpStatusCode.NotFound, (await page.GetAsync("/api/setup/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await page.PostAsJsonAsync("/api/setup/complete", Input())).StatusCode);
    }

    [Fact]
    public async Task A_failed_certificate_saves_nothing_and_leaves_setup_open()
    {
        _issuer.Fail = new CertificateException("validation_failed", "connection refused on port 80");
        var page = Page();
        Assert.Equal(HttpStatusCode.Accepted, (await page.PostAsJsonAsync("/api/setup/complete", Input())).StatusCode);
        var failed = await WaitAsync(page);
        Assert.Equal("validation_failed", failed.GetProperty("error").GetString());
        Assert.Contains("port 80", failed.GetProperty("detail").GetString());
        Assert.Equal(0, _restarter.Count);
        Assert.Equal(HttpStatusCode.OK, (await page.GetAsync("/api/setup/state")).StatusCode);
        var login = await _app.Browser().PostAsJsonAsync("/api/auth/login", new { username = "boss", password = "install-pass-1" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        _issuer.Fail = null;
        Assert.Equal(HttpStatusCode.Accepted, (await page.PostAsJsonAsync("/api/setup/complete", Input())).StatusCode);
        Assert.True((await WaitAsync(page)).TryGetProperty("url", out var url) && url.GetString() is not null);
    }
}

public sealed class SettingsKeyTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task The_openai_key_is_saved_shown_only_by_its_end_and_used()
    {
        var admin = await _app.AdminAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/settings", new { publicUrl = "", defaultModel = "", openAiKey = "sk-live-abcd9876" })).StatusCode);
        var s = await GateFactory.JsonAsync(await admin.GetAsync("/api/admin/settings"));
        Assert.Equal("…9876", s.GetProperty("openAiKey").GetString());
        Assert.DoesNotContain("abcd", s.GetRawText());
        var saved = _app.Services.GetRequiredService<UpstreamPool>().All.Single(u => u.Name == UpstreamPool.SavedKeyName);
        Assert.Equal("sk-live-abcd9876", saved.ApiKey);
        Assert.Equal("https://api.openai.com/v1/", saved.BaseUri.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/settings", new { publicUrl = "", defaultModel = "" })).StatusCode);
        Assert.Contains(_app.Services.GetRequiredService<UpstreamPool>().All, u => u.Name == UpstreamPool.SavedKeyName); // not sent: kept
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/settings", new { publicUrl = "", defaultModel = "", openAiKey = "" })).StatusCode);
        Assert.DoesNotContain(_app.Services.GetRequiredService<UpstreamPool>().All, u => u.Name == UpstreamPool.SavedKeyName);
    }
}

public class ManagedPortsTests
{
    private static async Task<HttpContext> RunAsync(int port, string path, ManagedState state, AcmeChallenges? challenges = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.LocalPort = port;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        var passed = false;
        var middleware = new ManagedPortsMiddleware(_ => { passed = true; return Task.CompletedTask; });
        var options = Options.Create(new GateOptions { Managed = new ManagedOptions { Enabled = true, PublicHttpsPort = 9443 } });
        await middleware.InvokeAsync(ctx, options, state, challenges ?? new AcmeChallenges());
        ctx.Items["passed"] = passed;
        return ctx;
    }

    private static string Body(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    [Fact]
    public async Task Port_80_answers_challenges_and_otherwise_redirects()
    {
        var challenges = new AcmeChallenges();
        challenges.Add("tok123", "tok123.thumb");
        var done = new ManagedState(true, "codex.example.test");
        var ctx = await RunAsync(ManagedOptions.HttpPort, "/.well-known/acme-challenge/tok123", done, challenges);
        Assert.Equal("tok123.thumb", Body(ctx));
        Assert.Equal(404, (await RunAsync(ManagedOptions.HttpPort, "/.well-known/acme-challenge/other", done, challenges)).Response.StatusCode);

        var redirect = await RunAsync(ManagedOptions.HttpPort, "/admin", done);
        Assert.Equal(301, redirect.Response.StatusCode);
        Assert.Equal("https://codex.example.test:9443/admin", redirect.Response.Headers.Location.ToString());
        Assert.Equal(404, (await RunAsync(ManagedOptions.HttpPort, "/admin", new ManagedState(false, null))).Response.StatusCode);
    }

    [Fact]
    public async Task The_setup_port_serves_only_the_setup_page()
    {
        var open = new ManagedState(false, null);
        var root = await RunAsync(ManagedOptions.SetupPort, "/", open);
        Assert.True((bool)root.Items["passed"]!);
        Assert.Equal("/setup.html", root.Request.Path.Value);
        Assert.True((bool)(await RunAsync(ManagedOptions.SetupPort, "/api/setup/state", open)).Items["passed"]!);
        Assert.Equal(404, (await RunAsync(ManagedOptions.SetupPort, "/api/admin/users", open)).Response.StatusCode);
        Assert.Equal(404, (await RunAsync(ManagedOptions.SetupPort, "/v1/responses", open)).Response.StatusCode);
    }

    [Fact]
    public async Task Setup_calls_exist_nowhere_else()
    {
        var done = new ManagedState(true, "codex.example.test");
        Assert.Equal(404, (await RunAsync(ManagedOptions.HttpsPort, "/api/setup/state", done)).Response.StatusCode);
        Assert.Equal(404, (await RunAsync(ManagedOptions.HttpsPort, "/setup.html", done)).Response.StatusCode);
        Assert.True((bool)(await RunAsync(ManagedOptions.HttpsPort, "/api/me/", done)).Items["passed"]!);
        Assert.True((bool)(await RunAsync(ManagedOptions.InternalPort, "/v1/responses", done)).Items["passed"]!);
    }
}

public class CloudflareDnsTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public bool Refuse { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {request.Headers.Authorization}");
            string json;
            if (Refuse)
            {
                json = "{\"success\":false,\"errors\":[{\"message\":\"Authentication error\"}]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            var q = request.RequestUri.Query;
            json = request.Method == HttpMethod.Get
                ? (q.Contains("name=example.test") ? "{\"success\":true,\"result\":[{\"id\":\"zone1\"}]}" : "{\"success\":true,\"result\":[]}")
                : "{\"success\":true,\"result\":{\"id\":\"rec9\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Finds_the_zone_adds_and_removes_the_record()
    {
        var handler = new Handler();
        var dns = new CloudflareDns(new Factory(handler));
        var (zone, record) = await dns.CreateTxtAsync("cf-token", "codex.example.test", "txt-value", CancellationToken.None);
        Assert.Equal(("zone1", "rec9"), (zone, record));
        await dns.DeleteAsync("cf-token", zone, record, CancellationToken.None);
        Assert.Equal(
        [
            "GET /client/v4/zones?name=codex.example.test Bearer cf-token",
            "GET /client/v4/zones?name=example.test Bearer cf-token",
            "POST /client/v4/zones/zone1/dns_records Bearer cf-token",
            "DELETE /client/v4/zones/zone1/dns_records/rec9 Bearer cf-token",
        ], handler.Calls);
    }

    [Fact]
    public async Task Reports_what_cloudflare_said()
    {
        var dns = new CloudflareDns(new Factory(new Handler { Refuse = true }));
        var ex = await Assert.ThrowsAsync<CertificateException>(() => dns.CreateTxtAsync("bad", "codex.example.test", "v", CancellationToken.None));
        Assert.Equal("cloudflare_failed", ex.Code);
        Assert.Contains("Authentication error", ex.Message);
    }
}

public sealed class RenewalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codexgate-renew-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Renews_only_when_less_than_30_days_are_left()
    {
        Directory.CreateDirectory(_dir);
        var db = new Database(Path.Combine(_dir, "t.db"));
        db.Migrate();
        var settings = new SettingsStore(db);
        await settings.SetAsync(SettingsStore.SetupDone, "1");
        await settings.SetAsync(SettingsStore.Domain, "codex.example.test");
        await settings.SetAsync(SettingsStore.CertMethod, CertMethods.Http);
        var certs = new CertificateStore(Path.Combine(_dir, "certs"));
        var issuer = new FakeIssuer();
        var renewal = new CertificateRenewal(Options.Create(new GateOptions()), settings, issuer, certs, TimeProvider.System,
            NullLogger<CertificateRenewal>.Instance);

        var fresh = FakeIssuer.SelfSigned("codex.example.test", 80);
        certs.Save(fresh.CertPem, fresh.KeyPem);
        Assert.Null(await renewal.RenewIfDueAsync(CancellationToken.None));
        Assert.Empty(issuer.Requests);

        var old = FakeIssuer.SelfSigned("codex.example.test", 10);
        certs.Save(old.CertPem, old.KeyPem);
        Assert.True(await renewal.RenewIfDueAsync(CancellationToken.None));
        Assert.Equal("codex.example.test", issuer.Requests.Single().Domain);
        Assert.True(certs.Current!.NotAfter > DateTime.Now.AddDays(60));
    }
}

public class AcmePemTests
{
    [Fact]
    public void Certes_chains_and_keys_become_pem_that_dotnet_loads()
    {
        var key = Certes.KeyFactory.NewKey(Certes.KeyAlgorithm.ES256);
        using var ec = ECDsa.Create();
        ec.ImportFromPem(AcmeIssuer.KeyPem(key));
        var req = new CertificateRequest("CN=codex.example.test", ec, HashAlgorithmName.SHA256);
        using var leaf = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var issuerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var issuer = new CertificateRequest("CN=Test CA", issuerKey, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // What an ACME server sends: leaf then issuer, CRLF line ends.
        var download = (leaf.ExportCertificatePem() + "\n" + issuer.ExportCertificatePem()).Replace("\n", "\r\n");
        var chain = new Certes.Acme.CertificateChain(download);

        var certPem = AcmeIssuer.ChainPem(chain);
        var keyPem = AcmeIssuer.KeyPem(key);
        using var loaded = CertificateStore.FromPem(certPem, keyPem);
        Assert.Equal("CN=codex.example.test", loaded.Subject);
        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(2, certPem.Split("BEGIN CERTIFICATE").Length - 1);
    }
}
