using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexGate.Security;
using Microsoft.AspNetCore.Http.Features;

namespace CodexGate.Gateway;

/// <summary>
/// Plans with "own ChatGPT login". Codex signs in with the user's own ChatGPT account and sends that
/// sign-in as its bearer token; the gateway key travels in <see cref="KeyHeader"/>. The gateway checks
/// the key, the plan and the user's own limits, relays the request unchanged to ChatGPT's Codex
/// backend, and records what it used. It never stores the ChatGPT token, and the owner's API key is
/// never involved.
/// </summary>
public sealed partial class GatewayHandler
{
    public const string KeyHeader = "X-CodexGate-Key";
    public const string ChatGptUpstreamName = "chatgpt";

    /// <summary>Request headers that stay here: hop-by-hop, the gateway's own, and anything about this hop.</summary>
    private static readonly HashSet<string> RelayDroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Proxy-Authorization",
        "Proxy-Connection", "Content-Length", "Content-Type", "Content-Encoding", "Accept-Encoding", "Cookie",
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Real-IP", "Origin", "Referer",
        KeyHeader, CsrfHeaderMiddleware.HeaderName,
    };

    /// <summary>Response headers not passed back: hop-by-hop, cookies, and what the gateway sets itself.</summary>
    private static readonly HashSet<string> RelayDroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "Content-Encoding", "Set-Cookie", "Alt-Svc",
        "Strict-Transport-Security", "Server",
    };

    public async Task RelayChatGptAsync(HttpContext ctx, string rest)
    {
        var now = Now();
        if (!RelayPath().IsMatch(rest) || rest.Contains("..", StringComparison.Ordinal) || rest.Contains("//", StringComparison.Ordinal))
        {
            await OpenAiError.WriteAsync(ctx, 404, "not_found", "Unknown path.");
            return;
        }
        var method = ctx.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsPost(method))
        {
            await OpenAiError.WriteAsync(ctx, 405, "method_not_allowed", "Only GET and POST are relayed.");
            return;
        }
        var caller = await AuthenticateAsync(ctx, now, ctx.Request.Headers[KeyHeader].ToString().Trim());
        if (caller is null)
        {
            return;
        }
        if (!caller.Plan.OwnLogin)
        {
            await OpenAiError.WriteAsync(ctx, 403, "own_login_not_enabled",
                "Your plan does not use your own ChatGPT sign-in. Set Codex up again from the CodexGate panel.", "permission_error");
            return;
        }
        if (BearerToken(ctx) is not { Length: > 0 })
        {
            await OpenAiError.WriteAsync(ctx, 401, "chatgpt_login_required",
                "Sign in to Codex with your ChatGPT account first: codex login (on a server: codex login --device-auth).");
            return;
        }

        var metered = HttpMethods.IsPost(method) && rest is "responses" or "responses/compact";
        if (!metered)
        {
            await RelayAsync(ctx, caller, rest, metered: false, now);
            return;
        }
        switch (concurrency.TryEnter(caller.User.Id, caller.Plan.MaxConcurrency, options.Value.MaxConcurrentRequests))
        {
            case ConcurrencyGate.Result.UserFull:
                ctx.Response.Headers.RetryAfter = "2";
                await OpenAiError.WriteAsync(ctx, 429, "rate_limit_exceeded",
                    $"Your plan allows {caller.Plan.MaxConcurrency} request(s) at a time; one has to finish first.", "rate_limit_error");
                return;
            case ConcurrencyGate.Result.GatewayFull:
                ctx.Response.Headers.RetryAfter = "2";
                await OpenAiError.WriteAsync(ctx, 503, "gateway_busy", "The gateway is busy; try again in a moment.", "server_error");
                return;
        }
        try
        {
            var othersRunning = Math.Max(0, concurrency.Current(caller.User.Id) - 1);
            if (await StopIfFullAsync(ctx, await WindowsAsync(caller, now, othersRunning), now))
            {
                return;
            }
            await RelayAsync(ctx, caller, rest, metered: true, now);
        }
        finally
        {
            concurrency.Exit(caller.User.Id);
        }
    }

    private async Task RelayAsync(HttpContext ctx, Caller caller, string rest, bool metered, long now)
    {
        byte[]? body = null;
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            body = await ReadRawBodyAsync(ctx);
            if (body is null)
            {
                return;
            }
        }
        var encoding = ctx.Request.Headers.ContentEncoding.ToString().Trim();
        var model = body is not null && encoding.Length == 0 ? ModelOf(body) : null;

        var baseUrl = options.Value.ChatGpt.BaseUrl.TrimEnd('/') + "/";
        var target = new Uri(new Uri(baseUrl), rest + ctx.Request.QueryString.Value);
        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (!RelayDroppedRequestHeaders.Contains(name) && !name.StartsWith(":", StringComparison.Ordinal))
            {
                request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(ctx.Request.ContentType, out var type)
                ? type : new MediaTypeHeaderValue("application/json");
            if (encoding.Length > 0)
            {
                request.Content.Headers.ContentEncoding.Add(encoding);
            }
        }

        using var upstreamCts = new CancellationTokenSource();
        var drain = TimeSpan.FromSeconds(Math.Max(0, options.Value.DrainSecondsAfterAbort));
        using var onAbort = ctx.RequestAborted.Register(() => upstreamCts.CancelAfter(drain));
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamCts.Token);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            log.LogWarning("ChatGPT could not be reached: {Error}", ex is HttpRequestException h ? h.HttpRequestError.ToString() : "Timeout");
            await OpenAiError.WriteAsync(ctx, 502, "upstream_unreachable", "ChatGPT could not be reached. Try again in a moment.", "server_error");
            return;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            var ok = status is >= 200 and < 300;
            var isSse = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            var sniffer = metered && ok ? new UsageSniffer(isSse) : null;
            long streamed = 0;
            try
            {
                // Every status goes back as it came: a 401 is how Codex learns to refresh its sign-in.
                ctx.Response.StatusCode = status;
                foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                {
                    if (!RelayDroppedResponseHeaders.Contains(name))
                    {
                        ctx.Response.Headers[name] = values.ToArray();
                    }
                }
                ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                var clientGone = false;
                var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(upstreamCts.Token);
                    int n;
                    while ((n = await stream.ReadAsync(buffer, upstreamCts.Token)) > 0)
                    {
                        sniffer?.Feed(buffer.AsSpan(0, n));
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
                if (!ctx.RequestAborted.IsCancellationRequested)
                {
                    log.LogWarning("Stream from ChatGPT broke after {Bytes} bytes: {Error}", streamed, ex.Message);
                }
            }
            finally
            {
                sniffer?.Complete();
            }

            if (metered)
            {
                var answered = sniffer?.Model ?? model ?? "unknown";
                var price = UsageMath.FindPrice(await prices.ListAsync(), answered);
                await RecordAsync(caller, ChatGptUpstreamName, rest, answered, price, 0, status, sniffer,
                    body?.Length ?? 0, streamed, isSse, started, now);
            }
        }
    }

    /// <summary>The body as sent (compressed or not), within the size limit.</summary>
    private async Task<byte[]?> ReadRawBodyAsync(HttpContext ctx)
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
        var copy = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (copy.Length + n > max)
                {
                    await TooLarge(ctx, max);
                    return null;
                }
                copy.Write(buffer, 0, n);
            }
            return copy.ToArray();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await TooLarge(ctx, max);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? ModelOf(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("model", out var m)
                && m.ValueKind == JsonValueKind.String && ModelPatterns.IsValid(m.GetString()!) ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9/_.-]{0,200}$")]
    private static partial Regex RelayPath();
}
