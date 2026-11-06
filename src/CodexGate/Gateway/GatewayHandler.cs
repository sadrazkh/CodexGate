using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexGate.Data;
using CodexGate.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace CodexGate.Gateway;

/// <summary>
/// The OpenAI-compatible endpoint Codex talks to. Each request is checked (key, account, plan,
/// body policy, model, quota, concurrency), sent to an upstream with the owner's key, streamed back
/// unchanged, and its usage recorded against the user.
/// </summary>
public sealed partial class GatewayHandler(
    UserStore users,
    PlanStore plans,
    KeyStore keys,
    UsageStore usage,
    PriceStore prices,
    SettingsStore settings,
    QuotaService quota,
    ConcurrencyGate concurrency,
    UpstreamPool pool,
    IHttpClientFactory http,
    IOptions<GateOptions> options,
    TimeProvider time,
    ILogger<GatewayHandler> log)
{
    public const string HttpClientName = "upstream";

    /// <summary>Request headers passed upstream; everything else (cookies, org/project, auth) stays here.</summary>
    private static readonly HashSet<string> ForwardRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "User-Agent", "OpenAI-Beta", "originator", "version", "session_id", "session-id", "thread-id",
        "conversation_id", "x-client-request-id", "traceparent", "tracestate",
    };

    /// <summary>Response headers passed back; the rest (rate limits, org, processing details) describe the owner's account.</summary>
    private static readonly HashSet<string> ForwardResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Cache-Control", "x-request-id", "Retry-After",
    };

    private sealed record Caller(UserRow User, PlanRow Plan, ApiKeyRow Key);

    public async Task ProxyAsync(HttpContext ctx, string path, bool requireStream)
    {
        var now = Now();
        var caller = await AuthenticateAsync(ctx, now);
        if (caller is null)
        {
            return;
        }
        var plan = caller.Plan;

        // The slot is taken before the body is read, so a user cannot pile up uploads in memory.
        switch (concurrency.TryEnter(caller.User.Id, plan.MaxConcurrency, options.Value.MaxConcurrentRequests))
        {
            case ConcurrencyGate.Result.UserFull:
                ctx.Response.Headers.RetryAfter = "2";
                await OpenAiError.WriteAsync(ctx, 429, "rate_limit_exceeded",
                    $"Your plan allows {plan.MaxConcurrency} request(s) at a time; one has to finish first.", "rate_limit_error");
                return;
            case ConcurrencyGate.Result.GatewayFull:
                ctx.Response.Headers.RetryAfter = "2";
                await OpenAiError.WriteAsync(ctx, 503, "gateway_busy", "The gateway is busy; try again in a moment.", "server_error");
                return;
        }
        try
        {
            await CheckAndForwardAsync(ctx, caller, path, requireStream, now);
        }
        finally
        {
            concurrency.Exit(caller.User.Id);
        }
    }

    private async Task CheckAndForwardAsync(HttpContext ctx, Caller caller, string path, bool requireStream, long now)
    {
        var plan = caller.Plan;
        var body = await ReadBodyAsync(ctx);
        if (body is null)
        {
            return;
        }

        var all = await settings.GetAllAsync();
        var allowWebSearch = all.GetValueOrDefault(SettingsStore.AllowWebSearch) == "1";
        var webSearchPrice = double.TryParse(all.GetValueOrDefault(SettingsStore.WebSearchPrice), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var p) ? p : SettingsStore.DefaultWebSearchPrice;
        var secret = all.GetValueOrDefault(SettingsStore.InstanceSecret) ?? "";

        var (accepted, refusal) = RequestPolicy.Apply(body.Value,
            new RequestPolicy.Options(requireStream, allowWebSearch, RequestPolicy.SafetyIdentifier(caller.User.Id, secret)));
        if (refusal is not null)
        {
            await OpenAiError.WriteAsync(ctx, refusal.Status, refusal.Code, refusal.Message);
            return;
        }
        var model = accepted!.Model;

        var allowed = ModelPatterns.Parse(plan.AllowedModels);
        if (!ModelPatterns.Allowed(allowed, model))
        {
            await OpenAiError.WriteAsync(ctx, 403, "model_not_allowed",
                $"Your plan does not include the model \"{model}\". Allowed: {ModelPatterns.Join(allowed)}.", "permission_error");
            return;
        }

        var price = UsageMath.FindPrice(await prices.ListAsync(), model);
        if (price is null && plan.Limits.Any(l => l.Kind == LimitKinds.Usd))
        {
            await OpenAiError.WriteAsync(ctx, 403, "price_not_configured",
                $"No price is set for \"{model}\", and your plan is limited in dollars. Ask the admin to add one.",
                "permission_error");
            return;
        }

        // Requests already running count too, at this user's recent average, so parallel requests
        // cannot all slip in under a nearly full limit.
        var othersRunning = Math.Max(0, concurrency.Current(caller.User.Id) - 1);
        var windows = await quota.EvaluateAsync(caller.User, plan.Limits, now, othersRunning);
        AddQuotaHeaders(ctx.Response, windows, now);
        var full = windows.FirstOrDefault(w => w.Exhausted);
        if (full is not null)
        {
            await OpenAiError.QuotaAsync(ctx, QuotaMessage(full), full.FreesAt, now);
            return;
        }

        await ForwardAsync(ctx, caller, path, accepted.Body, model, price, allowWebSearch ? webSearchPrice : 0, now);
    }

    public async Task ModelsAsync(HttpContext ctx)
    {
        var now = Now();
        var caller = await AuthenticateAsync(ctx, now);
        if (caller is null)
        {
            return;
        }
        var upstream = pool.All.FirstOrDefault();
        if (upstream is null)
        {
            await OpenAiError.WriteAsync(ctx, 503, "no_upstream", "No model API is configured on this gateway.", "server_error");
            return;
        }
        var allowed = ModelPatterns.Parse(caller.Plan.AllowedModels);
        try
        {
            using var request = BuildRequest(ctx, upstream, HttpMethod.Get, "models", null);
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ctx.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                await OpenAiError.WriteAsync(ctx, 502, "upstream_error",
                    $"The model API answered {(int)response.StatusCode} when listing models.", "server_error");
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ctx.RequestAborted));
            // Only names leave the gateway: "owned_by" and fine-tune ids carry the owner's organization.
            var data = doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray()
                    .Select(m => m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : null)
                    .Where(id => id is not null && !id.StartsWith("ft:", StringComparison.Ordinal)
                        && ModelPatterns.Allowed(allowed, id) && pool.CandidatesFor(id).Count > 0)
                    .Select(id => new { id, @object = "model" })
                    .ToList()
                : [];
            await ctx.Response.WriteAsJsonAsync(new { @object = "list", data });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            if (!ctx.RequestAborted.IsCancellationRequested)
            {
                await OpenAiError.WriteAsync(ctx, 502, "upstream_error", "The model API could not be reached.", "server_error");
            }
        }
    }

    private async Task<Caller?> AuthenticateAsync(HttpContext ctx, long now)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        ApiKeyRow? key = null;
        if (ApiKeys.LooksLikeKey(token))
        {
            key = await keys.FindActiveByHashAsync(ApiKeys.HashOf(token!));
        }
        var user = key is null ? null : await users.GetAsync(key.UserId);
        if (key is null || user is null)
        {
            await OpenAiError.WriteAsync(ctx, 401, "invalid_api_key",
                "The API key is missing, wrong or revoked. Create one in the CodexGate panel.");
            return null;
        }
        var plan = user.PlanId is { } planId ? await plans.GetAsync(planId) : null;
        var problem = Access.Check(user, plan, now);
        if (problem != AccessProblem.None)
        {
            var message = problem switch
            {
                AccessProblem.AccountDisabled => "Your account is disabled.",
                AccessProblem.SubscriptionExpired =>
                    $"Your subscription ended on {Utc(user.ExpiresAt!.Value)}. Ask the admin to renew it.",
                _ => "Your account has no plan yet. Ask the admin to assign one.",
            };
            await OpenAiError.WriteAsync(ctx, 403, Access.Code(problem), message, "permission_error");
            return null;
        }
        await keys.TouchAsync(key.Id, now);
        return new Caller(user, plan!, key);
    }

    private async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext ctx)
    {
        var max = Math.Max(1, options.Value.MaxRequestBodyMb) * 1024L * 1024L;
        if (ctx.Request.ContentLength > max)
        {
            await TooLarge(ctx, max);
            return null;
        }
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = max;
        }

        var encoding = ctx.Request.Headers.ContentEncoding.ToString().Trim().ToLowerInvariant();
        Stream source = ctx.Request.Body;
        switch (encoding)
        {
            case "":
            case "identity":
                break;
            case "gzip":
                source = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
                break;
            case "br":
                source = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
                break;
            case "deflate":
                source = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true);
                break;
            default:
                await OpenAiError.WriteAsync(ctx, 415, "unsupported_encoding",
                    $"Request bodies compressed with \"{encoding}\" are not supported; send them uncompressed.");
                return null;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var copy = new MemoryStream();
            int n;
            while ((n = await source.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (copy.Length + n > max)
                {
                    await TooLarge(ctx, max);
                    return null;
                }
                copy.Write(buffer, 0, n);
            }
            return copy.GetBuffer().AsMemory(0, (int)copy.Length);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await TooLarge(ctx, max);
            return null;
        }
        catch (InvalidDataException)
        {
            await OpenAiError.WriteAsync(ctx, 400, "invalid_body", "The compressed request body could not be read.");
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (!ReferenceEquals(source, ctx.Request.Body))
            {
                await source.DisposeAsync();
            }
        }
    }

    private static Task TooLarge(HttpContext ctx, long max) =>
        OpenAiError.WriteAsync(ctx, 413, "request_too_large", $"The request is larger than {max / 1024 / 1024} MiB.");

    private async Task ForwardAsync(
        HttpContext ctx, Caller caller, string path, byte[] body, string model, PriceRow? price, double webSearchPrice, long now)
    {
        var candidates = pool.CandidatesFor(model);
        if (candidates.Count == 0)
        {
            await OpenAiError.WriteAsync(ctx, 503, "no_upstream", $"No model API on this gateway serves \"{model}\".", "server_error");
            return;
        }

        // Upstream I/O has its own token: when Codex hangs up it is cancelled only after a grace
        // period, so an answer that is about to finish still reports what it used.
        using var upstreamCts = new CancellationTokenSource();
        var drain = TimeSpan.FromSeconds(Math.Max(0, options.Value.DrainSecondsAfterAbort));
        using var onAbort = ctx.RequestAborted.Register(() => upstreamCts.CancelAfter(drain));

        var client = http.CreateClient(HttpClientName);
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage? response = null;
        UpstreamPool.Upstream? upstream = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var last = i == candidates.Count - 1;
            var request = BuildRequest(ctx, candidate, HttpMethod.Post, path, body);
            var kept = false;
            pool.Begin(candidate);
            try
            {
                var r = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamCts.Token);
                if (IsRetryable(r.StatusCode))
                {
                    pool.Failed(candidate, $"HTTP {(int)r.StatusCode}", now);
                    if (!last)
                    {
                        r.Dispose();
                        continue;
                    }
                }
                response = r;
                upstream = candidate;
                kept = true;
                break;
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                return; // Codex left before any answer started.
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // A connect timeout surfaces as a cancellation that nobody asked for: an upstream failure.
                var what = ex is HttpRequestException h ? h.HttpRequestError.ToString() : "Timeout";
                pool.Failed(candidate, what, now);
                log.LogWarning("Upstream {Upstream} failed: {Error}", candidate.Name, what);
                if (last)
                {
                    await OpenAiError.WriteAsync(ctx, 502, "upstream_unreachable",
                        "The model API could not be reached. Try again in a moment.", "server_error");
                    return;
                }
            }
            finally
            {
                request.Dispose();
                if (!kept)
                {
                    pool.End(candidate);
                }
            }
        }
        if (response is null || upstream is null)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (status is 401 or 403)
        {
            // The owner's key was refused. Its error text can echo part of the key, so it never reaches users.
            log.LogError("Upstream {Upstream} refused the gateway's API key ({Status})", upstream.Name, status);
            pool.Failed(upstream, $"HTTP {status}", now);
            pool.End(upstream);
            response.Dispose();
            await OpenAiError.WriteAsync(ctx, 502, "upstream_auth_failed",
                "The gateway's own key for the model API was refused. Tell the admin.", "server_error");
            return;
        }
        if (status is < 200 or >= 300)
        {
            await RelayErrorAsync(ctx, response, upstreamCts.Token);
            pool.End(upstream);
            await RecordAsync(caller, upstream, path, model, price, webSearchPrice, status, null, body.Length, 0, false, started, now);
            return;
        }

        var isSse = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        var sniffer = new UsageSniffer(isSse);
        long streamed = 0;
        var clientGone = false;
        try
        {
            ctx.Response.StatusCode = status;
            CopyResponseHeaders(response, ctx.Response);
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(upstreamCts.Token);
                int n;
                while ((n = await stream.ReadAsync(buffer, upstreamCts.Token)) > 0)
                {
                    sniffer.Feed(buffer.AsSpan(0, n));
                    streamed += n;
                    if (clientGone)
                    {
                        continue;
                    }
                    try
                    {
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ctx.RequestAborted);
                        if (isSse)
                        {
                            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException)
                    {
                        clientGone = true;
                        upstreamCts.CancelAfter(drain);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            if (!clientGone && !ctx.RequestAborted.IsCancellationRequested)
            {
                pool.Failed(upstream, "Stream broke", now);
                log.LogWarning("Stream from {Upstream} broke after {Bytes} bytes: {Error}", upstream.Name, streamed, ex.Message);
            }
        }
        finally
        {
            sniffer.Complete();
            pool.End(upstream);
            response.Dispose();
        }

        await RecordAsync(caller, upstream, path, model, price, webSearchPrice, status, sniffer, body.Length, streamed, isSse, started, now);
    }

    /// <summary>Upstream errors go back with any organization, project or key ids blanked out.</summary>
    private static async Task RelayErrorAsync(HttpContext ctx, HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            var total = 0;
            int n;
            while (total < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
            {
                total += n;
            }
            text = Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            text = "";
        }
        finally
        {
            response.Dispose();
        }
        if (ctx.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        ctx.Response.StatusCode = (int)response.StatusCode;
        if (response.Headers.RetryAfter is { } retry)
        {
            ctx.Response.Headers.RetryAfter = retry.ToString();
        }
        ctx.Response.ContentType = "application/json";
        var scrubbed = Scrub(text);
        if (scrubbed.Length == 0)
        {
            scrubbed = JsonSerializer.Serialize(new
            {
                error = new { message = $"The model API answered {(int)response.StatusCode}.", type = "upstream_error", code = (string?)null },
            });
        }
        await ctx.Response.WriteAsync(scrubbed);
    }

    public static string Scrub(string text) => SecretIds().Replace(text, m => m.Groups[1].Value + "***");

    [GeneratedRegex(@"\b(org-|proj_|sk-|user-)[A-Za-z0-9_\-*]{4,}")]
    private static partial Regex SecretIds();

    private async Task RecordAsync(
        Caller caller, UpstreamPool.Upstream upstream, string path, string model, PriceRow? price, double webSearchPrice,
        int status, UsageSniffer? sniffer, long requestBytes, long responseBytes, bool isSse, long started, long now)
    {
        TokenUsage tokens;
        var estimated = false;
        if (sniffer?.Usage is { } reported)
        {
            tokens = reported;
        }
        else if (sniffer is not null && status is >= 200 and < 300)
        {
            // Cut short and never finished: the upstream still billed what it did.
            estimated = true;
            var output = isSse ? sniffer.DeltaEvents : UsageSniffer.EstimateTokens(responseBytes);
            tokens = new TokenUsage(UsageSniffer.EstimateTokens(requestBytes), 0, output, 0);
        }
        else
        {
            tokens = default;
        }

        var searches = sniffer?.WebSearchCalls ?? 0;
        var cost = UsageMath.Cost(tokens, price);
        if (searches > 0 && webSearchPrice > 0)
        {
            cost = (cost ?? 0) + searches * webSearchPrice;
        }
        var row = new UsageRow
        {
            UserId = caller.User.Id,
            KeyId = caller.Key.Id,
            At = now,
            Model = model,
            Upstream = upstream.Name,
            Endpoint = path,
            Status = status,
            InputTokens = tokens.Input,
            CachedTokens = tokens.Cached,
            OutputTokens = tokens.Output,
            ReasoningTokens = tokens.Reasoning,
            BillableTokens = UsageMath.BillableTokens(tokens, options.Value.CachedTokenWeight),
            CostUsd = cost,
            Estimated = estimated,
            ToolCalls = searches,
            DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
        try
        {
            await usage.InsertAsync(row);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record usage for user {UserId}", caller.User.Id);
        }
    }

    private static HttpRequestMessage BuildRequest(
        HttpContext ctx, UpstreamPool.Upstream upstream, HttpMethod method, string path, byte[]? body)
    {
        var request = new HttpRequestMessage(method, new Uri(upstream.BaseUri, path));
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (ForwardRequestHeaders.Contains(name) || name.StartsWith("x-codex-", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        foreach (var (name, value) in upstream.Headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstream.ApiKey);
        return request;
    }

    private static void CopyResponseHeaders(HttpResponseMessage from, HttpResponse to)
    {
        foreach (var (name, values) in from.Headers.Concat(from.Content.Headers))
        {
            if (ForwardResponseHeaders.Contains(name))
            {
                to.Headers[name] = values.ToArray();
            }
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// Codex reads these headers (as sent by ChatGPT) to show how much of a limit is used.
    /// The first window is "primary", the second "secondary".
    /// </summary>
    private static void AddQuotaHeaders(HttpResponse response, List<WindowStatus> windows, long now)
    {
        string[] slots = ["primary", "secondary"];
        for (var i = 0; i < Math.Min(slots.Length, windows.Count); i++)
        {
            var w = windows[i];
            var resetAfter = w.FreesAt is { } at ? Math.Max(0, (at - now) / 1000) : 0;
            var prefix = "x-codex-" + slots[i];
            response.Headers[prefix + "-used-percent"] = w.Percent.ToString("0.##", CultureInfo.InvariantCulture);
            response.Headers[prefix + "-window-minutes"] = (w.WindowHours * 60).ToString(CultureInfo.InvariantCulture);
            response.Headers[prefix + "-reset-after-seconds"] = resetAfter.ToString(CultureInfo.InvariantCulture);
            response.Headers[prefix + "-reset-at"] = ((now / 1000) + resetAfter).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string QuotaMessage(WindowStatus w)
    {
        var window = w.WindowHours % 24 == 0 ? $"{w.WindowHours / 24}-day" : $"{w.WindowHours}-hour";
        var amounts = w.Kind == LimitKinds.Usd
            ? $"${w.Used.ToString("0.00", CultureInfo.InvariantCulture)} of ${w.Amount.ToString("0.00", CultureInfo.InvariantCulture)}"
            : $"{w.Used.ToString("N0", CultureInfo.InvariantCulture)} of {w.Amount.ToString("N0", CultureInfo.InvariantCulture)} tokens";
        var frees = w.FreesAt is { } at ? $" It starts freeing up at {Utc(at)}." : "";
        return $"Your {window} quota is used up ({amounts}).{frees}";
    }

    private static string Utc(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();
}

public static class OpenAiError
{
    public static Task WriteAsync(HttpContext ctx, int status, string code, string message, string type = "invalid_request_error")
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = new { message, type, code, param = (string?)null } });
    }

    /// <summary>
    /// A used-up quota, in the shape ChatGPT uses for its own plan limits ("usage_limit_reached"
    /// with resets_at), so Codex stops at once and tells the user when to try again.
    /// </summary>
    public static Task QuotaAsync(HttpContext ctx, string message, long? freesAtMs, long nowMs)
    {
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        long? resetsAt = freesAtMs is { } f ? f / 1000 : null;
        long? resetsIn = freesAtMs is { } g ? Math.Max(0, (g - nowMs) / 1000) : null;
        return ctx.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type = "usage_limit_reached",
                code = "insufficient_quota",
                param = (string?)null,
                resets_at = resetsAt,
                resets_in_seconds = resetsIn,
            },
        });
    }
}

public static class GatewayEndpoints
{
    public const string RateLimitPolicy = "gateway";

    public static void MapGateway(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/v1").RequireRateLimiting(RateLimitPolicy);
        v1.MapPost("/responses", (HttpContext ctx, GatewayHandler h) => h.ProxyAsync(ctx, "responses", requireStream: true));
        v1.MapPost("/responses/compact", (HttpContext ctx, GatewayHandler h) => h.ProxyAsync(ctx, "responses/compact", requireStream: false));
        v1.MapGet("/models", (HttpContext ctx, GatewayHandler h) => h.ModelsAsync(ctx));
        v1.Map("/{**rest}", (HttpContext ctx) => OpenAiError.WriteAsync(ctx, 404, "not_found",
            "This gateway serves POST /v1/responses and GET /v1/models only."));
    }
}
