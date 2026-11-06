using System.Text.Json;

namespace CodexGate.Gateway;

/// <summary>
/// Watches the bytes of an upstream response as they pass through and pulls out the token usage:
/// from the final "response.completed" (or incomplete/failed) event of a server-sent-event stream,
/// or from the "usage" object of a plain JSON body. It never changes or delays the bytes.
/// </summary>
public sealed class UsageSniffer
{
    private const int MaxLineBytes = 32 * 1024 * 1024;
    private const int MaxJsonBytes = 16 * 1024 * 1024;

    private static readonly byte[] DataPrefix = "data:"u8.ToArray();

    private readonly bool _sse;
    private readonly MemoryStream _buffer = new();
    private bool _overflow;

    public UsageSniffer(bool sse)
    {
        _sse = sse;
    }

    public TokenUsage? Usage { get; private set; }

    /// <summary>Number of streamed "*.delta" events; each is roughly one output token. Used only to estimate.</summary>
    public long DeltaEvents { get; private set; }

    public bool SawTerminalEvent { get; private set; }

    /// <summary>Hosted web searches: "web_search_call" items in the final response, or their completion events.</summary>
    public long WebSearchCalls => Math.Max(_searchesInOutput, _searchEvents);

    private long _searchesInOutput;
    private long _searchEvents;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (!_sse)
        {
            if (_buffer.Length + chunk.Length <= MaxJsonBytes)
            {
                _buffer.Write(chunk);
            }
            else
            {
                _overflow = true;
            }
            return;
        }

        while (!chunk.IsEmpty)
        {
            var newline = chunk.IndexOf((byte)'\n');
            if (newline < 0)
            {
                Append(chunk);
                return;
            }
            Append(chunk[..newline]);
            EndLine();
            chunk = chunk[(newline + 1)..];
        }
    }

    /// <summary>Call once the body has ended so a final line without a newline, or a JSON body, is read.</summary>
    public void Complete()
    {
        if (_sse)
        {
            if (_buffer.Length > 0 || _overflow)
            {
                EndLine();
            }
            return;
        }
        if (!_overflow && _buffer.Length > 0)
        {
            var usage = TryReadUsage(_buffer.GetBuffer().AsSpan(0, (int)_buffer.Length), sse: false, out var searches);
            Usage ??= usage;
            _searchesInOutput = Math.Max(_searchesInOutput, searches);
        }
    }

    private void Append(ReadOnlySpan<byte> part)
    {
        if (_overflow)
        {
            return;
        }
        if (_buffer.Length + part.Length > MaxLineBytes)
        {
            _overflow = true;
            _buffer.SetLength(0);
            return;
        }
        _buffer.Write(part);
    }

    private void EndLine()
    {
        if (!_overflow && _buffer.Length > 0)
        {
            var line = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
            if (line[^1] == (byte)'\r')
            {
                line = line[..^1];
            }
            if (line.StartsWith(DataPrefix))
            {
                OnData(line[DataPrefix.Length..].TrimStart((byte)' '));
            }
        }
        _buffer.SetLength(0);
        _overflow = false;
    }

    private void OnData(ReadOnlySpan<byte> payload)
    {
        var type = PeekType(payload);
        if (type is null)
        {
            return;
        }
        if (type.EndsWith(".delta", StringComparison.Ordinal))
        {
            DeltaEvents++;
            return;
        }
        if (type == "response.web_search_call.completed")
        {
            _searchEvents++;
            return;
        }
        if (type is "response.completed" or "response.incomplete" or "response.failed" or "response.done")
        {
            SawTerminalEvent = true;
            Usage = TryReadUsage(payload, sse: true, out var searches) ?? Usage;
            _searchesInOutput = Math.Max(_searchesInOutput, searches);
        }
    }

    /// <summary>Reads the "type" of an event without parsing the whole (possibly large) payload.</summary>
    private static string? PeekType(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    return null;
                }
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
                {
                    var isType = reader.ValueTextEquals("type"u8);
                    reader.Read();
                    if (isType)
                    {
                        return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    reader.TrySkip();
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static TokenUsage? TryReadUsage(ReadOnlySpan<byte> json, bool sse, out long searches)
    {
        searches = 0;
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (sse || !root.TryGetProperty("usage", out _))
            {
                if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
                {
                    root = response;
                }
            }
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                searches = output.EnumerateArray().LongCount(item => item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "web_search_call");
            }
            return root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                ? ReadUsage(usage)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static TokenUsage ReadUsage(JsonElement usage)
    {
        var input = Number(usage, "input_tokens") ?? Number(usage, "prompt_tokens") ?? 0;
        var output = Number(usage, "output_tokens") ?? Number(usage, "completion_tokens") ?? 0;
        var cached = Nested(usage, "input_tokens_details", "cached_tokens")
            ?? Nested(usage, "prompt_tokens_details", "cached_tokens") ?? 0;
        var reasoning = Nested(usage, "output_tokens_details", "reasoning_tokens")
            ?? Nested(usage, "completion_tokens_details", "reasoning_tokens") ?? 0;
        return new TokenUsage(input, cached, output, reasoning);
    }

    private static long? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static long? Nested(JsonElement obj, string parent, string name) =>
        obj.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object ? Number(p, name) : null;

    /// <summary>A rough token count for text we never got usage for (about four bytes per token).</summary>
    public static long EstimateTokens(long bytes) => (bytes + 3) / 4;

}
