using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CodexGate.Api;
using CodexGate.Data;
using CodexGate.Gateway;
using CodexGate.Security;
using Microsoft.Extensions.Options;

namespace CodexGate.Hosting;

/// <summary>What the app knew about itself at startup in managed mode (it restarts after setup).</summary>
public sealed record ManagedState(bool SetupDone, string? Domain);

public interface IAppRestarter
{
    void Restart();
}

/// <summary>Ends the process; Docker's restart policy starts it again with the new listeners.</summary>
public sealed class LifetimeRestarter(IHostApplicationLifetime lifetime) : IAppRestarter
{
    public void Restart() => lifetime.StopApplication();
}

public sealed record SetupInput(
    string? Username, string? Password, string? Domain, string? Method, string? CloudflareToken, string? Email, string? OpenAiKey);

public sealed class SetupJob
{
    public List<string> Steps { get; } = [];
    public bool Running { get; set; } = true;
    public string? Error { get; set; }
    public string? Detail { get; set; }
    public string? Url { get; set; }
}

/// <summary>
/// The one-time setup of an installed server: certificate first (nothing is saved if that fails),
/// then the admin, the settings and the model key, then a restart into HTTPS. The page that drives
/// it only opens with the installer's token.
/// </summary>
public sealed partial class SetupService(
    IOptions<GateOptions> options, SettingsStore settings, UserStore users, ICertificateIssuer issuer,
    CertificateStore certs, UpstreamPool upstreams, IAppRestarter restarter, TimeProvider time, ILogger<SetupService> log)
{
    private readonly Lock _lock = new();
    private SetupJob? _job;

    private ManagedOptions Managed => options.Value.Managed;

    public SetupJob? Job
    {
        get
        {
            lock (_lock)
            {
                return _job;
            }
        }
    }

    /// <summary>A copy of the job for the page to show, taken under the lock the job writes with.</summary>
    public object? Snapshot()
    {
        lock (_lock)
        {
            return _job is not { } j ? null : new { running = j.Running, steps = j.Steps.ToArray(), error = j.Error, detail = j.Detail, url = j.Url };
        }
    }

    public async Task<bool> IsOpenAsync() =>
        Managed.Enabled && Managed.SetupToken.Length >= 16
        && (await settings.GetAllAsync()).GetValueOrDefault(SettingsStore.SetupDone) != "1";

    public bool TokenMatches(string? given) =>
        Managed.SetupToken.Length >= 16 && given is not null
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(Managed.SetupToken)));

    /// <summary>Checks the input and starts the job; returns an error code, or null when started.</summary>
    public string? Start(SetupInput input)
    {
        var username = Validation.Username(input.Username);
        if (username is null)
        {
            return "invalid_username";
        }
        if (!Validation.Password(input.Password))
        {
            return "weak_password";
        }
        var domain = (input.Domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (!DomainName().IsMatch(domain))
        {
            return "invalid_domain";
        }
        var method = input.Method ?? "";
        if (method == CertMethods.Http && !Managed.HttpChallengeAvailable)
        {
            return "http_unavailable";
        }
        if (method == CertMethods.Cloudflare && (string.IsNullOrWhiteSpace(input.CloudflareToken)
                || input.CloudflareToken.Trim().Length > 200 || input.CloudflareToken.Trim().Any(char.IsWhiteSpace)))
        {
            return "invalid_cloudflare_token";
        }
        if (method is not (CertMethods.Http or CertMethods.Cloudflare))
        {
            return "invalid_method";
        }
        var email = (input.Email ?? "").Trim();
        if (email.Length > 0 && (email.Length > 254 || !email.Contains('@') || email.Any(char.IsWhiteSpace)))
        {
            return "invalid_email";
        }
        var key = (input.OpenAiKey ?? "").Trim();
        if (key.Length > 400 || key.Any(char.IsWhiteSpace))
        {
            return "invalid_key";
        }

        SetupJob job;
        lock (_lock)
        {
            if (_job is { Running: true })
            {
                return "busy";
            }
            _job = job = new SetupJob();
        }
        var request = new IssueRequest(domain, method, method == CertMethods.Cloudflare ? input.CloudflareToken!.Trim() : null,
            email.Length > 0 ? email : null);
        _ = Task.Run(() => RunAsync(job, request, username, input.Password!, key));
        return null;
    }

    private async Task RunAsync(SetupJob job, IssueRequest request, string username, string password, string openAiKey)
    {
        void Step(string code)
        {
            lock (_lock)
            {
                job.Steps.Add(code);
            }
        }
        try
        {
            var cert = await issuer.IssueAsync(request, Step, CancellationToken.None);
            certs.Save(cert.CertPem, cert.KeyPem);

            Step("admin");
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            var existing = await users.GetByUsernameAsync(username);
            if (existing is null)
            {
                await users.InsertAsync(new UserRow
                {
                    Username = username,
                    DisplayName = "Admin",
                    PasswordHash = PasswordHasher.Hash(password),
                    Role = Roles.Admin,
                    Enabled = true,
                    SecurityStamp = ApiKeys.NewStamp(),
                    CreatedAt = now,
                });
            }
            else
            {
                existing.Role = Roles.Admin;
                existing.Enabled = true;
                existing.PasswordHash = PasswordHasher.Hash(password);
                existing.SecurityStamp = ApiKeys.NewStamp();
                await users.UpdateAsync(existing);
            }

            var port = Managed.PublicHttpsPort;
            var url = "https://" + request.Domain + (port == 443 ? "" : ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await settings.SetAsync(SettingsStore.Domain, request.Domain);
            await settings.SetAsync(SettingsStore.CertMethod, request.Method);
            await settings.SetAsync(SettingsStore.CloudflareToken, request.CloudflareToken);
            await settings.SetAsync(SettingsStore.AcmeEmail, request.Email);
            await settings.SetAsync(SettingsStore.PublicUrl, url);
            if (openAiKey.Length > 0)
            {
                await settings.SetAsync(SettingsStore.OpenAiKey, openAiKey);
                await upstreams.ReloadAsync();
            }
            await settings.SetAsync(SettingsStore.SetupDone, "1");

            lock (_lock)
            {
                job.Url = url;
                job.Steps.Add("done");
            }
            log.LogInformation("Setup finished for {Domain}; restarting into HTTPS", request.Domain);
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => restarter.Restart(), TaskScheduler.Default);
        }
        catch (CertificateException ex)
        {
            Fail(job, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Setup failed");
            Fail(job, "certificate_failed", (ex as Certes.AcmeRequestException)?.Error?.Detail ?? ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                job.Running = false;
            }
        }
    }

    private void Fail(SetupJob job, string code, string detail)
    {
        lock (_lock)
        {
            job.Error = code;
            job.Detail = detail;
        }
    }

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex DomainName();
}

/// <summary>The setup page's calls. All of them answer 404 without the installer's token.</summary>
public static class SetupApi
{
    public const string TokenHeader = "X-Setup-Token";

    /// <summary>The page polls progress every 1.5 s; that must not use up the sign-in limit the other calls share.</summary>
    public const string ProgressRateLimit = "setup-progress";

    public static void MapSetupApi(this IEndpointRouteBuilder app)
    {
        var setup = app.MapGroup("/api/setup");
        setup.MapGet("/state", async (HttpContext ctx, SetupService service, IOptions<GateOptions> options) =>
        {
            if (!await OpenAsync(ctx, service))
            {
                return Errors.NotFound();
            }
            var m = options.Value.Managed;
            return Results.Ok(new { httpsPort = m.PublicHttpsPort, httpAvailable = m.HttpChallengeAvailable, publicIp = m.PublicIp });
        }).RequireRateLimiting("login");
        setup.MapPost("/domain", async (DomainInput input, HttpContext ctx, SetupService service, IOptions<GateOptions> options) =>
        {
            if (!await OpenAsync(ctx, service))
            {
                return Errors.NotFound();
            }
            var domain = (input.Domain ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            string[] addresses;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                addresses = (await Dns.GetHostAddressesAsync(domain, timeout.Token)).Select(a => a.ToString()).Distinct().ToArray();
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or OperationCanceledException)
            {
                addresses = [];
            }
            var ip = options.Value.Managed.PublicIp;
            return Results.Ok(new { addresses, pointsHere = ip.Length > 0 && addresses.Contains(ip) });
        }).RequireRateLimiting("login");
        setup.MapPost("/complete", async (SetupInput input, HttpContext ctx, SetupService service) =>
        {
            if (!await OpenAsync(ctx, service))
            {
                return Errors.NotFound();
            }
            var error = service.Start(input);
            return error is null ? Results.Accepted() : Errors.Json(error == "busy" ? 409 : 400, error);
        }).RequireRateLimiting("login");
        setup.MapGet("/progress", (HttpContext ctx, SetupService service) =>
        {
            var snapshot = service.Snapshot();
            return snapshot is null || !service.TokenMatches(ctx.Request.Headers[TokenHeader]) ? Errors.NotFound() : Results.Ok(snapshot);
        }).RequireRateLimiting(ProgressRateLimit);
    }

    private static async Task<bool> OpenAsync(HttpContext ctx, SetupService service) =>
        service.TokenMatches(ctx.Request.Headers[TokenHeader]) && await service.IsOpenAsync();

    public sealed record DomainInput(string? Domain);
}

/// <summary>
/// In managed mode each container port has one job: the setup port serves only the setup page, the
/// port-80 mapping only ACME challenges and a redirect, and the setup calls exist nowhere else.
/// </summary>
public sealed class ManagedPortsMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> SetupFiles = new(StringComparer.Ordinal)
    {
        "/setup.html", "/setup.js", "/i18n-setup.js", "/app.css", "/favicon.svg", "/healthz",
    };

    public async Task InvokeAsync(HttpContext ctx, IOptions<GateOptions> options, ManagedState state, AcmeChallenges challenges)
    {
        var port = ctx.Connection.LocalPort;
        if (!options.Value.Managed.Enabled || port == 0)
        {
            await next(ctx);
            return;
        }
        var path = ctx.Request.Path.Value ?? "/";
        switch (port)
        {
            case ManagedOptions.HttpPort:
                const string prefix = "/.well-known/acme-challenge/";
                if (path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var answer = challenges.Answer(path[prefix.Length..]);
                    if (answer is null)
                    {
                        ctx.Response.StatusCode = 404;
                        return;
                    }
                    ctx.Response.ContentType = "text/plain";
                    await ctx.Response.WriteAsync(answer);
                    return;
                }
                if (state.SetupDone && state.Domain is { } domain)
                {
                    var p = options.Value.Managed.PublicHttpsPort;
                    ctx.Response.Redirect($"https://{domain}{(p == 443 ? "" : ":" + p)}{path}{ctx.Request.QueryString}", permanent: true);
                    return;
                }
                ctx.Response.StatusCode = 404;
                return;
            case ManagedOptions.SetupPort:
                if (path == "/")
                {
                    ctx.Request.Path = "/setup.html";
                }
                else if (!SetupFiles.Contains(path) && !path.StartsWith("/api/setup/", StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                break;
            default:
                if (path.StartsWith("/api/setup", StringComparison.Ordinal) || path is "/setup.html" or "/setup.js" or "/i18n-setup.js")
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }
                break;
        }
        await next(ctx);
    }
}

/// <summary>Renews the certificate when it has less than 30 days left, without a restart.</summary>
public sealed class CertificateRenewal(
    IOptions<GateOptions> options, SettingsStore settings, ICertificateIssuer issuer, CertificateStore certs,
    TimeProvider time, ILogger<CertificateRenewal> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Managed.Enabled)
        {
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = TimeSpan.FromHours(12);
                try
                {
                    if (await RenewIfDueAsync(stoppingToken) == false)
                    {
                        wait = TimeSpan.FromHours(1); // failed: try again sooner
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Certificate renewal failed");
                    wait = TimeSpan.FromHours(1);
                }
                await Task.Delay(wait, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>True when renewed, null when not due, false when it should have and could not.</summary>
    public async Task<bool?> RenewIfDueAsync(CancellationToken ct)
    {
        var all = await settings.GetAllAsync();
        if (all.GetValueOrDefault(SettingsStore.SetupDone) != "1" || all.GetValueOrDefault(SettingsStore.Domain) is not { } domain)
        {
            return null;
        }
        var current = certs.Current;
        if (current is not null && current.NotAfter.ToUniversalTime() - time.GetUtcNow().UtcDateTime > TimeSpan.FromDays(30))
        {
            return null;
        }
        log.LogInformation("Renewing the certificate for {Domain}", domain);
        var cert = await issuer.IssueAsync(new IssueRequest(domain, all.GetValueOrDefault(SettingsStore.CertMethod) ?? CertMethods.Http,
            all.GetValueOrDefault(SettingsStore.CloudflareToken), all.GetValueOrDefault(SettingsStore.AcmeEmail)), _ => { }, ct);
        certs.Save(cert.CertPem, cert.KeyPem);
        return true;
    }
}
