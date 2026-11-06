using Microsoft.Extensions.Options;

namespace CodexGate.Gateway;

/// <summary>The configured model APIs, with live counters for picking the least busy one.</summary>
public sealed class UpstreamPool
{
    private readonly Lock _lock = new();

    public UpstreamPool(IOptions<GateOptions> options, ILogger<UpstreamPool> log)
    {
        var list = new List<Upstream>();
        var index = 0;
        foreach (var o in options.Value.Upstreams)
        {
            index++;
            if (string.IsNullOrWhiteSpace(o.BaseUrl) || string.IsNullOrWhiteSpace(o.ApiKey))
            {
                log.LogWarning("Upstream #{Index} skipped: BaseUrl and ApiKey are both required", index);
                continue;
            }
            if (!Uri.TryCreate(o.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
                || baseUri.Scheme is not ("https" or "http"))
            {
                log.LogWarning("Upstream #{Index} skipped: BaseUrl is not an http(s) URL", index);
                continue;
            }
            list.Add(new Upstream(
                string.IsNullOrWhiteSpace(o.Name) ? $"upstream-{index}" : o.Name.Trim(),
                baseUri,
                o.ApiKey.Trim(),
                o.Models.SelectMany(ModelPatterns.Parse).ToList(),
                new Dictionary<string, string>(o.Headers, StringComparer.OrdinalIgnoreCase)));
        }
        All = list;
        if (list.Count == 0)
        {
            log.LogWarning("No upstream is configured; Codex requests will get 503 until Gate:Upstreams is set");
        }
    }

    public IReadOnlyList<Upstream> All { get; }

    /// <summary>Upstreams that serve the model, least busy first, config order breaking ties.</summary>
    public List<Upstream> CandidatesFor(string model)
    {
        lock (_lock)
        {
            return All
                .Select((u, i) => (u, i))
                .Where(x => ModelPatterns.Allowed(x.u.Models, model))
                .OrderBy(x => x.u.InFlight)
                .ThenBy(x => x.i)
                .Select(x => x.u)
                .ToList();
        }
    }

    public void Begin(Upstream u)
    {
        lock (_lock)
        {
            u.InFlight++;
            u.Requests++;
        }
    }

    public void End(Upstream u)
    {
        lock (_lock)
        {
            u.InFlight = Math.Max(0, u.InFlight - 1);
        }
    }

    public void Failed(Upstream u, string error, long now)
    {
        lock (_lock)
        {
            u.LastError = error;
            u.LastErrorAt = now;
        }
    }

    public sealed class Upstream(string name, Uri baseUri, string apiKey, List<string> models, Dictionary<string, string> headers)
    {
        public string Name { get; } = name;
        public Uri BaseUri { get; } = baseUri;
        public string ApiKey { get; } = apiKey;
        public List<string> Models { get; } = models;
        public Dictionary<string, string> Headers { get; } = headers;
        public int InFlight { get; set; }
        public long Requests { get; set; }
        public string? LastError { get; set; }
        public long? LastErrorAt { get; set; }
    }
}
