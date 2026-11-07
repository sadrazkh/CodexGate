using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Certes;
using Certes.Acme;
using AuthorizationStatus = Certes.Acme.Resource.AuthorizationStatus;
using Microsoft.Extensions.Options;

namespace CodexGate.Hosting;

/// <summary>The HTTPS certificate in use; swapped in place when renewed, so no restart is needed.</summary>
public sealed class CertificateStore(string directory)
{
    private X509Certificate2? _current;

    public X509Certificate2? Current => Volatile.Read(ref _current);

    public string CertPath => Path.Combine(directory, "cert.pem");

    public string KeyPath => Path.Combine(directory, "key.pem");

    public bool Load()
    {
        if (!File.Exists(CertPath) || !File.Exists(KeyPath))
        {
            return false;
        }
        Volatile.Write(ref _current, FromPem(File.ReadAllText(CertPath), File.ReadAllText(KeyPath)));
        return true;
    }

    public void Save(string certPem, string keyPem)
    {
        var cert = FromPem(certPem, keyPem); // refuse to store something unusable
        Directory.CreateDirectory(directory);
        WritePrivate(CertPath, certPem);
        WritePrivate(KeyPath, keyPem);
        Volatile.Write(ref _current, cert);
    }

    /// <summary>A certificate with its key, re-imported so the key is usable by TLS on every platform.</summary>
    public static X509Certificate2 FromPem(string certPem, string keyPem)
    {
        using var pem = X509Certificate2.CreateFromPem(certPem, keyPem);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }

    private static void WritePrivate(string path, string text)
    {
        var tmp = path + ".tmp";
        File.Delete(tmp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var writer = new StreamWriter(new FileStream(tmp, options)))
        {
            writer.Write(text);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Answers for HTTP-01 challenges in flight (token → key authorization).</summary>
public sealed class AcmeChallenges
{
    private readonly ConcurrentDictionary<string, string> _answers = new();

    public void Add(string token, string keyAuthorization) => _answers[token] = keyAuthorization;

    public void Remove(string token) => _answers.TryRemove(token, out _);

    public string? Answer(string token) => _answers.TryGetValue(token, out var a) ? a : null;
}

public static class CertMethods
{
    public const string Http = "http";
    public const string Cloudflare = "cloudflare";
}

public sealed record IssueRequest(string Domain, string Method, string? CloudflareToken, string? Email);

public sealed record IssuedCertificate(string CertPem, string KeyPem);

public interface ICertificateIssuer
{
    Task<IssuedCertificate> IssueAsync(IssueRequest request, Action<string> progress, CancellationToken ct);
}

/// <summary>
/// Gets a certificate from Let's Encrypt (or any ACME server). HTTP-01 is answered by this app on
/// the port the installer mapped to the server's port 80; DNS-01 adds and removes a TXT record
/// through the Cloudflare API, which needs no port at all.
/// </summary>
public sealed class AcmeIssuer(
    IOptions<GateOptions> options, AcmeChallenges challenges, CloudflareDns cloudflare, ILogger<AcmeIssuer> log)
    : ICertificateIssuer
{
    public async Task<IssuedCertificate> IssueAsync(IssueRequest request, Action<string> progress, CancellationToken ct)
    {
        var managed = options.Value.Managed;
        var directory = new Uri(managed.AcmeDirectory);
        var http = managed.AcmeInsecureTls
            ? new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) // test CAs only
            : new HttpClient();
        using var _ = http;
        var accountKey = LoadOrCreateAccountKey();
        var acme = new AcmeContext(directory, accountKey, new AcmeHttpClient(directory, http));

        progress("account");
        await acme.NewAccount(string.IsNullOrWhiteSpace(request.Email) ? [] : [$"mailto:{request.Email.Trim()}"], true);

        progress("order");
        var order = await acme.NewOrder([request.Domain]);
        var authz = (await order.Authorizations()).First();

        string? httpToken = null;
        (string Zone, string Record)? txt = null;
        try
        {
            IChallengeContext challenge;
            if (request.Method == CertMethods.Cloudflare)
            {
                challenge = await authz.Dns();
                progress("dns");
                txt = await cloudflare.CreateTxtAsync(request.CloudflareToken!, request.Domain,
                    acme.AccountKey.DnsTxt(challenge.Token), ct);
                await Task.Delay(TimeSpan.FromSeconds(managed.DnsWaitSeconds), ct);
            }
            else
            {
                challenge = await authz.Http();
                httpToken = challenge.Token;
                challenges.Add(challenge.Token, challenge.KeyAuthz);
            }

            progress("validate");
            await challenge.Validate();
            for (var i = 0; ; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var state = await authz.Resource();
                if (state.Status == AuthorizationStatus.Valid)
                {
                    break;
                }
                if (state.Status == AuthorizationStatus.Invalid || i > 60)
                {
                    var detail = state.Challenges?.Select(c => c.Error?.Detail).FirstOrDefault(d => !string.IsNullOrEmpty(d));
                    throw new CertificateException("validation_failed", detail ?? state.Status?.ToString() ?? "invalid");
                }
            }
        }
        finally
        {
            if (httpToken is not null)
            {
                challenges.Remove(httpToken);
            }
            if (txt is { } record)
            {
                try
                {
                    await cloudflare.DeleteAsync(request.CloudflareToken!, record.Zone, record.Record, CancellationToken.None);
                }
                catch (Exception ex) when (ex is HttpRequestException or CertificateException)
                {
                    log.LogWarning("Could not remove the ACME TXT record: {Error}", ex.Message);
                }
            }
        }

        progress("certificate");
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var chain = await order.Generate(new CsrInfo { CommonName = request.Domain }, key);
        var certPem = chain.Certificate.ToPem() + string.Concat(chain.Issuers.Select(i => i.ToPem()));
        return new IssuedCertificate(certPem, key.ToPem());
    }

    private IKey LoadOrCreateAccountKey()
    {
        var dir = Path.Combine(Bootstrap.DataDirOf(options.Value), "acme");
        var path = Path.Combine(dir, "account.pem");
        if (File.Exists(path))
        {
            return KeyFactory.FromPem(File.ReadAllText(path));
        }
        Directory.CreateDirectory(dir);
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        File.WriteAllText(path, key.ToPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return key;
    }
}

public sealed class CertificateException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;
}

/// <summary>Adds and removes the DNS-01 TXT record through the Cloudflare API (token with Zone → DNS → Edit).</summary>
public sealed class CloudflareDns(IHttpClientFactory http)
{
    public const string HttpClientName = "cloudflare";
    private static readonly Uri Api = new("https://api.cloudflare.com/client/v4/");

    public async Task<(string Zone, string Record)> CreateTxtAsync(string token, string domain, string value, CancellationToken ct)
    {
        var zone = await FindZoneAsync(token, domain, ct);
        using var request = Request(token, HttpMethod.Post, $"zones/{zone}/dns_records");
        request.Content = JsonContent.Create(new { type = "TXT", name = "_acme-challenge." + domain, content = value, ttl = 60 });
        var result = await SendAsync(request, ct);
        var id = result.GetProperty("result").GetProperty("id").GetString()
            ?? throw new CertificateException("cloudflare_failed", "No record id.");
        return (zone, id);
    }

    public async Task DeleteAsync(string token, string zone, string record, CancellationToken ct)
    {
        using var request = Request(token, HttpMethod.Delete, $"zones/{zone}/dns_records/{record}");
        await SendAsync(request, ct);
    }

    /// <summary>The zone is the longest suffix of the domain that the token can see.</summary>
    private async Task<string> FindZoneAsync(string token, string domain, CancellationToken ct)
    {
        var labels = domain.Split('.');
        for (var i = 0; i < labels.Length - 1; i++)
        {
            var candidate = string.Join('.', labels[i..]);
            using var request = Request(token, HttpMethod.Get, $"zones?name={Uri.EscapeDataString(candidate)}");
            var result = await SendAsync(request, ct);
            var zones = result.GetProperty("result");
            if (zones.GetArrayLength() > 0)
            {
                return zones[0].GetProperty("id").GetString()!;
            }
        }
        throw new CertificateException("cloudflare_zone_not_found",
            $"The Cloudflare token cannot see a zone for {domain}.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        return request;
    }

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new CertificateException("cloudflare_failed", $"Cloudflare answered {(int)response.StatusCode}.");
        }
        if (!response.IsSuccessStatusCode || !root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
        {
            var message = root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0
                ? errors[0].GetProperty("message").GetString()
                : $"HTTP {(int)response.StatusCode}";
            throw new CertificateException("cloudflare_failed", "Cloudflare: " + message);
        }
        return root;
    }
}
