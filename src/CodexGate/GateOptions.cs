namespace CodexGate;

/// <summary>Settings bound from the "Gate" configuration section (env: Gate__Name).</summary>
public sealed class GateOptions
{
    /// <summary>
    /// Folder for the SQLite database, cookie keys and the first admin password. Not "data": on
    /// case-insensitive file systems that is the Data source folder.
    /// </summary>
    public string DataDir { get; set; } = "codexgate-data";

    /// <summary>Username of the first admin, created only while no admin exists.</summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// Password of the first admin. When empty, a random one is written to
    /// DataDir/initial-admin-password.txt instead of being logged.
    /// </summary>
    public string? AdminPassword { get; set; }

    /// <summary>The model APIs requests are sent to. At least one is needed to serve Codex.</summary>
    public List<UpstreamOptions> Upstreams { get; set; } = [];

    /// <summary>How much a cached input token counts against a token limit (0..1).</summary>
    public double CachedTokenWeight { get; set; } = 0.1;

    /// <summary>Largest request body Codex may send, in MiB.</summary>
    public int MaxRequestBodyMb { get; set; } = 32;

    /// <summary>Gateway requests in flight across all users; more get 503 so memory stays bounded.</summary>
    public int MaxConcurrentRequests { get; set; } = 64;

    /// <summary>After Codex hangs up mid-answer, keep reading this long so the final usage still arrives.</summary>
    public int DrainSecondsAfterAbort { get; set; } = 15;

    /// <summary>Honour X-Forwarded-For / X-Forwarded-Proto. Turn on only behind your own reverse proxy.</summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>
    /// Addresses of that proxy (IPs or CIDR ranges). Empty means any sender is believed, which is only
    /// safe when the gateway's own port cannot be reached except through the proxy.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>Codex sessions on this server (needs Linux, tmux, Codex and the root helper; see docs/remote.md).</summary>
    public RemoteOptions Remote { get; set; } = new();

    /// <summary>Installed by install.sh: the app owns its ports, runs the setup page, and gets its own certificate.</summary>
    public ManagedOptions Managed { get; set; } = new();

    /// <summary>Where plans with "own ChatGPT login" send Codex's requests, with the user's own sign-in.</summary>
    public ChatGptOptions ChatGpt { get; set; } = new();
}

public sealed class ChatGptOptions
{
    /// <summary>The Codex backend of ChatGPT, as Codex itself uses it when signed in with ChatGPT.</summary>
    public string BaseUrl { get; set; } = "https://chatgpt.com/backend-api/codex";
}

public sealed class ManagedOptions
{
    // Ports inside the container; install.sh maps the server's ports onto them.
    public const int InternalPort = 8080; // loopback only: Codex in server sessions
    public const int HttpPort = 8081;     // the server's port 80: ACME HTTP-01 and a redirect to HTTPS
    public const int SetupPort = 8082;    // the one-time setup page
    public const int HttpsPort = 8443;    // the panel and the gateway

    public bool Enabled { get; set; }

    /// <summary>Opens the setup page; printed once by the installer, compared in constant time.</summary>
    public string SetupToken { get; set; } = "";

    /// <summary>The server port mapped to HttpsPort (shown in addresses).</summary>
    public int PublicHttpsPort { get; set; } = 8443;

    /// <summary>Whether the server's port 80 is mapped to HttpPort, so HTTP-01 can work.</summary>
    public bool HttpChallengeAvailable { get; set; }

    /// <summary>The server's public IP, as the installer saw it; used to check the domain's DNS.</summary>
    public string PublicIp { get; set; } = "";

    public string AcmeDirectory { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>Trust any TLS certificate of the ACME server. Only for a test CA such as Pebble.</summary>
    public bool AcmeInsecureTls { get; set; }

    /// <summary>How long to let a new DNS-01 TXT record spread before asking for validation.</summary>
    public int DnsWaitSeconds { get; set; } = 20;
}

public sealed class RemoteOptions
{
    public bool Enabled { get; set; }

    /// <summary>The root helper that creates server accounts and runs tmux as them.</summary>
    public string Helper { get; set; } = "/usr/local/libexec/codexgate-host";

    /// <summary>Run the helper through "sudo -n" (CodexGate itself should not run as root).</summary>
    public bool UseSudo { get; set; } = true;

    /// <summary>The gateway address Codex inside server sessions uses; normally this server on loopback.</summary>
    public string GatewayUrl { get; set; } = "http://127.0.0.1:8080/v1";
}

public sealed class UpstreamOptions
{
    public string Name { get; set; } = "";

    /// <summary>Base of an OpenAI-compatible Responses API, e.g. https://api.openai.com/v1.</summary>
    public string BaseUrl { get; set; } = "";

    public string ApiKey { get; set; } = "";

    /// <summary>Model patterns this upstream serves ("gpt-5*"); empty means every model.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>Extra headers sent upstream, e.g. OpenAI-Project.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];
}
