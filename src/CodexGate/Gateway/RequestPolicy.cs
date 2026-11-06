using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexGate.Gateway;

/// <summary>
/// Reads a Responses API request, refuses what the gateway cannot meter, and rewrites the rest so
/// the upstream receives exactly what was checked: one canonical JSON body with no duplicate or
/// look-alike keys, server-side storage off, the gateway's tool policy applied, and a per-user
/// safety identifier so abuse reports point at the teammate rather than the whole organization.
/// </summary>
public static class RequestPolicy
{
    /// <summary>Tool types that only describe the client's own functions; the API bills nothing extra for them.</summary>
    public static readonly HashSet<string> ClientTools = new(StringComparer.Ordinal)
    {
        "function", "custom", "local_shell", "tool_search",
    };

    /// <summary>Hosted search the API bills per call; kept only when the admin allows it.</summary>
    public static readonly HashSet<string> WebSearchTools = new(StringComparer.Ordinal)
    {
        "web_search", "web_search_preview",
    };

    private static readonly string[] Checked =
    [
        "model", "stream", "background", "service_tier", "store", "tools", "tool_choice", "previous_response_id",
        "conversation", "prompt", "safety_identifier",
    ];

    private static readonly string[] Refused = ["previous_response_id", "conversation", "prompt"];

    public sealed record Accepted(string Model, byte[] Body, IReadOnlyList<string> DroppedTools);

    public sealed record Refusal(int Status, string Code, string Message);

    public sealed record Options(bool RequireStream, bool AllowWebSearch, string SafetyIdentifier);

    public static (Accepted? Ok, Refusal? Error) Apply(ReadOnlyMemory<byte> body, Options options)
    {
        JsonObject root;
        try
        {
            var node = JsonNode.Parse(body.Span, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (node is not JsonObject obj)
            {
                return Refuse(400, "invalid_json", "The request body is not a JSON object.");
            }
            root = obj;
        }
        catch (JsonException)
        {
            return Refuse(400, "invalid_json", "The request body is not valid JSON, or a key appears twice.");
        }

        // A key that only differs in case from one we check could be read differently upstream.
        foreach (var (name, _) in root)
        {
            var twin = Checked.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            if (twin is not null && twin != name)
            {
                return Refuse(400, "invalid_request", $"Unexpected key \"{name}\".");
            }
        }

        if (!TryString(root, "model", out var model) || model is null || model.Trim().Length == 0)
        {
            return Refuse(400, "missing_model", "The request has no \"model\" string.");
        }
        model = model.Trim();
        if (!ModelPatterns.IsValid(model) || model.Contains('*'))
        {
            return Refuse(400, "invalid_model", "The model name is not valid.");
        }

        if (!TryBool(root, "stream", out var stream) || !TryBool(root, "background", out var background))
        {
            return Refuse(400, "invalid_request", "\"stream\" and \"background\" must be true or false.");
        }
        if (options.RequireStream && stream != true)
        {
            return Refuse(400, "stream_required", "This gateway serves streamed responses only (\"stream\": true).");
        }
        if (background == true)
        {
            return Refuse(400, "background_not_supported", "Background responses are not available through this gateway.");
        }

        if (!TryString(root, "service_tier", out var tier) || tier is not (null or "auto" or "default" or "flex"))
        {
            return Refuse(400, "service_tier_not_allowed",
                "Only the default service tier is available through this gateway (no fast/priority mode).");
        }

        foreach (var key in Refused)
        {
            if (root.TryGetPropertyValue(key, out var value) && value is not null)
            {
                return Refuse(400, "not_supported", $"\"{key}\" is not supported by this gateway; send the full input instead.");
            }
        }

        var dropped = new List<string>();
        if (root.TryGetPropertyValue("tools", out var toolsNode) && toolsNode is not null)
        {
            if (toolsNode is not JsonArray tools)
            {
                return Refuse(400, "invalid_request", "\"tools\" must be an array.");
            }
            for (var i = tools.Count - 1; i >= 0; i--)
            {
                var type = tools[i] is JsonObject t && TryString(t, "type", out var s) ? s : null;
                var allowed = type is not null
                    && (ClientTools.Contains(type) || (options.AllowWebSearch && WebSearchTools.Contains(type)));
                if (!allowed)
                {
                    dropped.Add(type ?? "?");
                    tools.RemoveAt(i);
                }
            }
            if (root["tool_choice"] is JsonObject choice && TryString(choice, "type", out var chosen)
                && chosen is not null && dropped.Contains(chosen))
            {
                root["tool_choice"] = "auto";
            }
        }

        root["model"] = model;
        root["store"] = false;
        root["safety_identifier"] = options.SafetyIdentifier;

        var bytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        return (new Accepted(model, bytes, dropped), null);
    }

    /// <summary>A stable, non-reversible id per user for the API's abuse attribution.</summary>
    public static string SafetyIdentifier(long userId, string secret) =>
        "cg-" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(userId.ToString(System.Globalization.CultureInfo.InvariantCulture))))[..24].ToLowerInvariant();

    private static (Accepted?, Refusal?) Refuse(int status, string code, string message) => (null, new Refusal(status, code, message));

    /// <summary>True when the key is absent, null or a string; false when it holds another type.</summary>
    private static bool TryString(JsonObject obj, string key, out string? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return true;
        }
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            value = v.GetValue<string>();
            return true;
        }
        return false;
    }

    /// <summary>True when the key is absent, null or a boolean; false when it holds another type.</summary>
    private static bool TryBool(JsonObject obj, string key, out bool? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return true;
        }
        var kind = node.GetValueKind();
        if (kind is JsonValueKind.True or JsonValueKind.False)
        {
            value = kind == JsonValueKind.True;
            return true;
        }
        return false;
    }
}
