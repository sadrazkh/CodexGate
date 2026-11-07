using CodexGate.Data;
using Microsoft.Extensions.Options;

namespace CodexGate.Gateway;

/// <summary>
/// The model APIs: those in configuration, plus the OpenAI key saved from the setup page or the
/// panel's settings. Live counters pick the least busy one.
/// </summary>
public sealed class UpstreamPool
{
    public const string SavedKeyName = "openai";
    public static readonly Uri OpenAiBase = new("https://api.openai.com/v1/");

    private readonly Lock _lock = new();
    private readonly IOptions<GateOptions> _options;
    private readonly SettingsStore _settings;
    private readonly ILogger<UpstreamPool> _log;
    private List<Upstream> _all = [];

    public UpstreamPool(IOptions<GateOptions> options, SettingsStore settings, ILogger<UpstreamPool> log)
    {
        _options = options;
        _settings = settings;
        _log = log;
        Reload(settings.GetAllAsync().GetAwaiter().GetResult().GetValueOrDefault(SettingsStore.OpenAiKey));
    }

    /// <summary>Rebuilds the list after the saved key changed; requests in flight keep their upstream.</summary>
    public async Task ReloadAsync() => Reload((await _settings.GetAllAsync()).GetValueOrDefault(SettingsStore.OpenAiKey));

    private void Reload(string? savedKey)
    {
        var options = _options;
        var log = _log;
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
        if (!string.IsNullOrWhiteSpace(savedKey))
        {
            list.Add(new Upstream(SavedKeyName, OpenAiBase, savedKey.Trim(), [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }
        lock (_lock)
        {
            _all = list;
        }
        if (list.Count == 0)
        {
            log.LogWarning("No model API yet: add an OpenAI key in Settings or set Gate:Upstreams");
        }
    }

    public IReadOnlyList<Upstream> All
    {
        get
        {
            lock (_lock)
            {
                return _all;
            }
        }
    }

    /// <summary>Upstreams that serve the model, least busy first, config order breaking ties.</summary>
    public List<Upstream> CandidatesFor(string model)
    {
        lock (_lock)
        {
            return _all
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
