using System.Net;
using System.Threading.RateLimiting;
using CodexGate;
using CodexGate.Api;
using CodexGate.Data;
using CodexGate.Gateway;
using CodexGate.Hosting;
using CodexGate.Remote;
using CodexGate.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.Configure<GateOptions>(builder.Configuration.GetSection("Gate"));
services.PostConfigure<GateOptions>(o => o.DataDir = Path.GetFullPath(o.DataDir, builder.Environment.ContentRootPath));
services.AddSingleton(TimeProvider.System);
services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<GateOptions>>().Value;
    return new Database(Path.Combine(Bootstrap.DataDir(options, sp), "codexgate.db"));
});
services.AddSingleton<UserStore>();
services.AddSingleton<PlanStore>();
services.AddSingleton<KeyStore>();
services.AddSingleton<UsageStore>();
services.AddSingleton<PriceStore>();
services.AddSingleton<SettingsStore>();
services.AddSingleton<QuotaService>();
services.AddSingleton<ConcurrencyGate>();
services.AddSingleton<UpstreamPool>();
services.AddSingleton<GatewayHandler>();
services.AddSingleton<PanelViews>();
services.AddSingleton<LoginGuard>();
services.AddSingleton<RemoteStore>();
services.AddSingleton<TerminalTickets>();
services.AddSingleton<IRemoteHost, TmuxRemoteHost>();
services.AddSingleton<RemoteService>();
services.AddSingleton(sp => new CertificateStore(Path.Combine(Bootstrap.DataDirOf(sp.GetRequiredService<IOptions<GateOptions>>().Value), "certs")));
services.AddSingleton<AcmeChallenges>();
services.AddSingleton<CloudflareDns>();
services.AddSingleton<ICertificateIssuer, AcmeIssuer>();
services.AddSingleton<IAppRestarter, LifetimeRestarter>();
services.AddSingleton<SetupService>();
services.AddSingleton<CertificateRenewal>();
services.AddHostedService(sp => sp.GetRequiredService<CertificateRenewal>());
services.AddHttpClient(CloudflareDns.HttpClientName);

// Installed with install.sh: the app opens its own ports. Before setup: the setup page; after it:
// HTTPS with the certificate setup got. Port 80's mapping always answers ACME challenges.
var startupGate = builder.Configuration.GetSection("Gate").Get<GateOptions>() ?? new GateOptions();
var managedState = new ManagedState(false, null);
CertificateStore? httpsCertificates = null;
if (startupGate.Managed.Enabled)
{
    var dataDir = Path.GetFullPath(startupGate.DataDir, builder.Environment.ContentRootPath);
    Directory.CreateDirectory(dataDir);
    var startupDb = new Database(Path.Combine(dataDir, "codexgate.db"));
    startupDb.Migrate();
    var saved = await new SettingsStore(startupDb).GetAllAsync();
    var hasCertificate = new CertificateStore(Path.Combine(dataDir, "certs")).Load();
    managedState = new ManagedState(saved.GetValueOrDefault(SettingsStore.SetupDone) == "1" && hasCertificate,
        saved.GetValueOrDefault(SettingsStore.Domain));
    var done = managedState.SetupDone;
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.Listen(IPAddress.Loopback, ManagedOptions.InternalPort);
        k.Listen(IPAddress.Any, ManagedOptions.HttpPort);
        if (done)
        {
            k.Listen(IPAddress.Any, ManagedOptions.HttpsPort, l => l.UseHttps(h =>
                h.ServerCertificateSelector = (_, _) => httpsCertificates?.Current));
        }
        else
        {
            k.Listen(IPAddress.Any, ManagedOptions.SetupPort);
        }
    });
}
services.AddSingleton(managedState);

// Cookie keys live next to the database so sign-ins survive restarts and redeploys.
services.AddDataProtection().SetApplicationName("CodexGate");
services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((o, sp) =>
{
    var options = sp.GetRequiredService<IOptions<GateOptions>>().Value;
    var dir = new DirectoryInfo(Path.Combine(Bootstrap.DataDir(options, sp), "keys"));
    o.XmlRepository = new FileSystemXmlRepository(dir, NullLoggerFactory.Instance);
});

services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "codexgate";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    o.Events.OnValidatePrincipal = Claims.ValidateCookieAsync;
});
services.AddAuthorizationBuilder().AddPolicy("admin", p => p.RequireRole(Roles.Admin));

services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Codex makes a few requests a minute (more in tool loops); this only stops floods from one address.
    o.AddPolicy(GatewayEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.OnRejected = (ctx, ct) => new ValueTask(ctx.HttpContext.Request.Path.StartsWithSegments("/v1")
        ? ctx.HttpContext.Response.WriteAsJsonAsync(new { error = new { message = "Too many requests from this address.", type = "rate_limit_error", code = "rate_limit_exceeded" } }, ct)
        : ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "too_many_attempts" }, ct));
});

services.AddHttpClient(GatewayHandler.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
        UseCookies = false,
        AllowAutoRedirect = false, // never carry the owner's key to wherever a redirect points
    })
    .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

var app = builder.Build();

app.Services.GetRequiredService<Database>().Migrate();
await Bootstrap.EnsureAdminAsync(app.Services);
httpsCertificates = app.Services.GetRequiredService<CertificateStore>();
httpsCertificates.Load();

var gate = app.Services.GetRequiredService<IOptions<GateOptions>>().Value;
if (gate.TrustForwardedHeaders)
{
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var entry in gate.TrustedProxies.SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
    {
        if (entry.Contains('/'))
        {
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
        }
        else
        {
            forwarded.KnownProxies.Add(IPAddress.Parse(entry));
        }
    }
    if (gate.TrustedProxies.Count == 0)
    {
        app.Logger.LogWarning("TrustForwardedHeaders is on with no Gate:TrustedProxies: X-Forwarded-* is believed from anyone who can reach this port");
    }
    app.UseForwardedHeaders(forwarded);
}

app.UseMiddleware<ManagedPortsMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(25) });
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfHeaderMiddleware>();

app.MapGet("/healthz", () => Results.Text("ok"));
app.MapAccountApi();
app.MapAdminApi();
app.MapRemoteApi();
app.MapSetupApi();
app.MapGateway();
app.Map("/api/{**rest}", () => Errors.NotFound());

app.Run();

public partial class Program;
