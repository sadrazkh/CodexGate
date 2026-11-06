using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CodexGate.Tests;

public sealed class GatewayTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<(HttpClient Codex, HttpClient Panel, HttpClient Admin, long UserId, long PlanId)> SetupAsync(object? plan = null)
    {
        var admin = await _app.AdminAsync();
        var planId = await GateFactory.CreatePlanAsync(admin, plan ?? new
        {
            name = "Standard",
            maxConcurrency = 2,
            allowedModels = "gpt-5*",
            limits = new[] { new { windowHours = 5, kind = "tokens", amount = 1_000_000 } },
        });
        var userId = await GateFactory.CreateUserAsync(admin, "ali", planId);
        var (codex, panel, _) = await _app.CodexForAsync("ali");
        return (codex, panel, admin, userId, planId);
    }

    [Fact]
    public async Task Streams_the_upstream_answer_unchanged_and_records_usage()
    {
        var (codex, panel, _, _, _) = await SetupAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = GateFactory.ResponsesBody() };
        request.Headers.Add("OpenAI-Organization", "org-attacker");
        request.Headers.Add("session_id", "abc-123");
        request.Headers.Add("Cookie", "codexgate=stolen");

        var res = await codex.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var text = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakeUpstream.Completed(100, 40, 20), text);
        Assert.True(res.Headers.Contains("x-request-id"));
        Assert.False(res.Headers.Contains("OpenAI-Organization"));
        Assert.False(res.Headers.Contains("Set-Cookie"));
        Assert.True(res.Headers.Contains("x-codex-primary-used-percent"));

        var sent = Assert.Single(_app.Upstream.Requests);
        Assert.Equal("https://up1.test/v1/responses", sent.Url);
        Assert.Equal("Bearer sk-upstream-one", sent.Authorization);
        Assert.Equal("abc-123", sent.Headers["session_id"]);
        Assert.False(sent.Headers.ContainsKey("OpenAI-Organization"));
        Assert.False(sent.Headers.ContainsKey("Cookie"));
        Assert.Contains("\"model\":\"gpt-5.5\"", sent.Body);

        var usage = await GateFactory.JsonAsync(await panel.GetAsync("/api/me/usage"));
        var row = Assert.Single(usage.GetProperty("recent").EnumerateArray());
        Assert.Equal(100, row.GetProperty("inputTokens").GetInt64());
        Assert.Equal(40, row.GetProperty("cachedTokens").GetInt64());
        Assert.Equal(20, row.GetProperty("outputTokens").GetInt64());
        Assert.Equal(60 + 40 * 0.1 + 20, row.GetProperty("billableTokens").GetDouble(), 6);
        Assert.False(row.GetProperty("estimated").GetBoolean());
    }

    [Fact]
    public async Task Rejects_missing_wrong_and_revoked_keys()
    {
        var (codex, panel, _, _, _) = await SetupAsync();
        var anonymous = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);

        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "cgk_" + new string('A', 43));
        var wrong = await anonymous.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_api_key", (await GateFactory.JsonAsync(wrong)).GetProperty("error").GetProperty("code").GetString());

        var keys = await GateFactory.JsonAsync(await panel.GetAsync("/api/me/keys"));
        var id = keys[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await panel.DeleteAsync("/api/me/keys/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
        Assert.Empty(_app.Upstream.Requests);
    }

    [Fact]
    public async Task Stops_a_user_whose_quota_is_used_up_until_the_window_moves_on()
    {
        var (codex, _, _, _, _) = await SetupAsync(new
        {
            name = "Tiny",
            limits = new[] { new { windowHours = 5, kind = "tokens", amount = 100 } },
        });
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(FakeUpstream.Completed(150, 0, 10)));

        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
        var refused = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var error = (await GateFactory.JsonAsync(refused)).GetProperty("error");
        Assert.Equal("insufficient_quota", error.GetProperty("code").GetString());
        // The shape ChatGPT uses for plan limits: Codex stops at once and says when to try again.
        Assert.Equal("usage_limit_reached", error.GetProperty("type").GetString());
        Assert.True(error.GetProperty("resets_at").GetInt64() > 0);
        Assert.Contains("5-hour quota is used up", error.GetProperty("message").GetString());
        Assert.Equal("100", refused.Headers.GetValues("x-codex-primary-used-percent").Single());
        Assert.Single(_app.Upstream.Requests);

        _app.Clock.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
    }

    [Fact]
    public async Task An_admin_reset_lets_the_user_go_on_and_keeps_history()
    {
        var (codex, _, admin, userId, _) = await SetupAsync(new
        {
            name = "Tiny",
            limits = new[] { new { windowHours = 24, kind = "tokens", amount = 50 } },
        });
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);

        _app.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/users/{userId}/reset-usage", null)).StatusCode);
        _app.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);

        var report = await GateFactory.JsonAsync(await admin.GetAsync("/api/admin/usage"));
        Assert.Equal(2, report.GetArrayLength());
    }

    [Fact]
    public async Task Dollar_limits_count_cost_and_refuse_models_without_a_price()
    {
        var (codex, _, admin, _, _) = await SetupAsync(new
        {
            name = "Dollars",
            limits = new[] { new { windowHours = 720, kind = "usd", amount = 1.0 } },
        });
        var unpriced = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.Forbidden, unpriced.StatusCode);
        Assert.Equal("price_not_configured", (await GateFactory.JsonAsync(unpriced)).GetProperty("error").GetProperty("code").GetString());

        var prices = new[] { new { model = "gpt-5*", inputPerM = 10_000.0, cachedPerM = 0.0, outputPerM = 0.0 } };
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/prices", prices)).StatusCode);
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(FakeUpstream.Completed(200, 0, 0)));

        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode); // $2.00
        Assert.Equal(HttpStatusCode.TooManyRequests, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
    }

    [Fact]
    public async Task Refuses_models_outside_the_plan_and_unsafe_request_options()
    {
        var (codex, _, _, _, _) = await SetupAsync();
        var model = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody("o3"));
        Assert.Equal(HttpStatusCode.Forbidden, model.StatusCode);
        Assert.Equal("model_not_allowed", (await GateFactory.JsonAsync(model)).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody(extra: new { background = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody(extra: new { service_tier = "priority" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody("=cmd|x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await codex.PostAsync("/v1/responses", new StringContent("[1,2]", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await codex.PostAsync("/v1/responses", new StringContent("{\"input\":\"x\"}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Empty(_app.Upstream.Requests);
    }

    [Fact]
    public async Task Only_responses_and_models_are_reachable()
    {
        var (codex, _, _, _, _) = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await codex.GetAsync("/v1/responses/resp_123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await codex.PostAsync("/v1/files", new StringContent("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await codex.PostAsync("/v1/chat/completions", GateFactory.ResponsesBody())).StatusCode);
        Assert.Empty(_app.Upstream.Requests);
    }

    [Fact]
    public async Task Expired_disabled_and_planless_accounts_are_refused()
    {
        var (codex, _, admin, userId, _) = await SetupAsync();
        _app.Clock.Advance(TimeSpan.FromDays(31));
        var expired = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        Assert.Equal("subscription_expired", (await GateFactory.JsonAsync(expired)).GetProperty("error").GetProperty("code").GetString());

        // A month later the admin's own 7-day panel cookie has run out too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/me/")).StatusCode);
        admin = await _app.AdminAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/admin/users/{userId}/renew", new { days = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/admin/users/{userId}", new { planId = 0 })).StatusCode);
        var planless = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal("no_plan", (await GateFactory.JsonAsync(planless)).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/admin/users/{userId}", new { enabled = false })).StatusCode);
        var disabled = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal("account_disabled", (await GateFactory.JsonAsync(disabled)).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Holds_a_user_to_the_plans_concurrency()
    {
        var (codex, _, _, _, _) = await SetupAsync(new { name = "One at a time", maxConcurrency = 1 });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = Encoding.UTF8.GetBytes(FakeUpstream.Completed(10, 0, 1));
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(new HeldStream(full[..20], release, full[20..])));

        var first = await codex.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = GateFactory.ResponsesBody() },
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("rate_limit_exceeded", (await GateFactory.JsonAsync(second)).GetProperty("error").GetProperty("code").GetString());

        release.SetResult();
        Assert.Equal(FakeUpstream.Completed(10, 0, 1), await first.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
    }

    [Fact]
    public async Task Fails_over_to_the_next_upstream_when_one_is_down()
    {
        var (codex, _, admin, _, _) = await SetupAsync();
        _app.Upstream.Responder = (req, _) => Task.FromResult(req.RequestUri!.Host == "up1.test"
            ? FakeUpstream.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"overloaded\"}}")
            : FakeUpstream.Sse(FakeUpstream.Completed(5, 0, 1)));

        var res = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(["https://up1.test/v1/responses", "https://up2.test/v1/responses"], _app.Upstream.Requests.Select(r => r.Url));
        Assert.Equal("Bearer sk-upstream-two", _app.Upstream.Requests.Last().Authorization);

        var overview = await GateFactory.JsonAsync(await admin.GetAsync("/api/admin/overview"));
        var up1 = overview.GetProperty("upstreams").EnumerateArray().First(u => u.GetProperty("name").GetString() == "primary");
        Assert.Equal("HTTP 503", up1.GetProperty("lastError").GetString());
        Assert.DoesNotContain("sk-upstream", overview.GetRawText());
    }

    [Fact]
    public async Task Never_shows_users_the_upstreams_auth_error()
    {
        var (codex, _, _, _, _) = await SetupAsync();
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Json(HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Incorrect API key provided: sk-up****-one\"}}"));
        var res = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("upstream_auth_failed", text);
        Assert.DoesNotContain("sk-up", text);
    }

    [Fact]
    public async Task Passes_upstream_client_errors_through_and_records_them_without_tokens()
    {
        var (codex, panel, _, _, _) = await SetupAsync();
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Json(HttpStatusCode.BadRequest,
            "{\"error\":{\"message\":\"Unsupported parameter\",\"code\":\"unsupported_parameter\"}}"));
        var res = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Unsupported parameter", await res.Content.ReadAsStringAsync());
        var row = (await GateFactory.JsonAsync(await panel.GetAsync("/api/me/usage"))).GetProperty("recent")[0];
        Assert.Equal(400, row.GetProperty("status").GetInt32());
        Assert.Equal(0, row.GetProperty("billableTokens").GetDouble());
    }

    [Fact]
    public async Task A_stream_the_client_abandons_is_charged_an_estimate()
    {
        var (codex, panel, _, _, _) = await SetupAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var head = Encoding.UTF8.GetBytes(
            "data: {\"type\":\"response.created\",\"response\":{}}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"a\"}\n\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"b\"}\n\n");
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(new HeldStream(head, release, [])));

        using var cts = new CancellationTokenSource();
        var res = await codex.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = GateFactory.ResponsesBody() },
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await res.Content.ReadAsStreamAsync();
        var buffer = new byte[head.Length];
        var read = 0;
        while (read < head.Length)
        {
            read += await stream.ReadAsync(buffer.AsMemory(read));
        }
        await cts.CancelAsync();
        res.Dispose();

        JsonElement row = default;
        for (var i = 0; i < 50; i++)
        {
            var recent = (await GateFactory.JsonAsync(await panel.GetAsync("/api/me/usage"))).GetProperty("recent");
            if (recent.GetArrayLength() > 0)
            {
                row = recent[0];
                break;
            }
            await Task.Delay(100);
        }
        Assert.Equal(JsonValueKind.Object, row.ValueKind);
        Assert.True(row.GetProperty("estimated").GetBoolean());
        Assert.Equal(2, row.GetProperty("outputTokens").GetInt64());
        Assert.True(row.GetProperty("inputTokens").GetInt64() > 0);
    }

    [Fact]
    public async Task Accepts_a_gzip_body_and_forwards_it_plain()
    {
        var (codex, _, _, _, _) = await SetupAsync();
        var json = "{\"model\":\"gpt-5.5\",\"stream\":true,\"input\":\"compressed\"}";
        var packed = new MemoryStream();
        await using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            await gz.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        var content = new ByteArrayContent(packed.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", content)).StatusCode);
        Assert.Equal("compressed", JsonDocument.Parse(_app.Upstream.Requests.Single().Body).RootElement.GetProperty("input").GetString());

        var zstd = new ByteArrayContent([1, 2, 3]);
        zstd.Headers.ContentEncoding.Add("zstd");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await codex.PostAsync("/v1/responses", zstd)).StatusCode);
    }

    [Fact]
    public async Task Lists_only_the_models_the_plan_allows()
    {
        var (codex, _, _, _, _) = await SetupAsync();
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Json(HttpStatusCode.OK,
            "{\"object\":\"list\",\"data\":[{\"id\":\"gpt-5.5\"},{\"id\":\"o3\"},{\"id\":\"gpt-5.4-mini\"}]}"));
        var models = await GateFactory.JsonAsync(await codex.GetAsync("/v1/models"));
        Assert.Equal(["gpt-5.5", "gpt-5.4-mini"], models.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
    }
}
