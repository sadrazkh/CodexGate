using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodexGate.Gateway;
using CodexGate.Security;

namespace CodexGate.Tests;

/// <summary>What the gateway refuses, rewrites and hides: the findings of the security review.</summary>
public sealed class PolicyTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<(HttpClient Codex, HttpClient Panel, HttpClient Admin)> SetupAsync(object? plan = null)
    {
        var admin = await _app.AdminAsync();
        var planId = await GateFactory.CreatePlanAsync(admin, plan ?? new { name = "Open", maxConcurrency = 2 });
        await GateFactory.CreateUserAsync(admin, "ali", planId);
        var (codex, panel, _) = await _app.CodexForAsync("ali");
        return (codex, panel, admin);
    }

    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<string?> CodeOf(HttpResponseMessage res) =>
        (await GateFactory.JsonAsync(res)).GetProperty("error").GetProperty("code").GetString();

    [Theory]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"model\":\"o3\"}", "invalid_json")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"Background\":true}", "invalid_request")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"background\":\"true\"}", "invalid_request")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":false}", "stream_required")]
    [InlineData("{\"model\":\"gpt-5.5\"}", "stream_required")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"previous_response_id\":\"resp_x\"}", "not_supported")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"conversation\":\"conv_x\"}", "not_supported")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"prompt\":{\"id\":\"pmpt_x\"}}", "not_supported")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"service_tier\":\"scale\"}", "service_tier_not_allowed")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"service_tier\":7}", "service_tier_not_allowed")]
    [InlineData("{\"model\":\"gpt-5.5\",\"stream\":true,\"tools\":{}}", "invalid_request")]
    public async Task Refuses_requests_it_could_not_meter_or_that_could_be_read_two_ways(string body, string code)
    {
        var (codex, _, _) = await SetupAsync();
        var res = await codex.PostAsync("/v1/responses", Raw(body));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(code, await CodeOf(res));
        Assert.Empty(_app.Upstream.Requests);
    }

    [Fact]
    public async Task Rewrites_the_body_storage_off_identity_on_web_search_out()
    {
        var (codex, _, _) = await SetupAsync();
        var body = """
            {"model":"gpt-5.5","stream":true,"store":true,"service_tier":"flex","safety_identifier":"spoofed",
             "tool_choice":{"type":"web_search"},
             "tools":[{"type":"function","name":"exec_command"},{"type":"custom","name":"apply_patch"},
                      {"type":"web_search"},{"type":"code_interpreter","container":{"type":"auto"}},{"type":"tool_search"}]}
            """;
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", Raw(body))).StatusCode);

        var sent = JsonDocument.Parse(_app.Upstream.Requests.Single().Body).RootElement;
        Assert.False(sent.GetProperty("store").GetBoolean());
        Assert.StartsWith("cg-", sent.GetProperty("safety_identifier").GetString());
        Assert.Equal("auto", sent.GetProperty("tool_choice").GetString());
        Assert.Equal(["function", "custom", "tool_search"], sent.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("type").GetString()));
        Assert.Equal("flex", sent.GetProperty("service_tier").GetString());
    }

    [Fact]
    public async Task Web_search_when_allowed_is_kept_counted_and_priced()
    {
        var (codex, panel, admin) = await SetupAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/settings",
            new { publicUrl = "", defaultModel = "", allowWebSearch = true, webSearchPrice = 0.5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/prices",
            new[] { new { model = "gpt-5.5", inputPerM = 0.0, cachedPerM = 0.0, outputPerM = 0.0 } })).StatusCode);
        var completed = "data: {\"type\":\"response.completed\",\"response\":{\"output\":[{\"type\":\"web_search_call\"},{\"type\":\"web_search_call\"},{\"type\":\"message\"}]," +
                        "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}\n\n";
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(completed));

        var body = "{\"model\":\"gpt-5.5\",\"stream\":true,\"tools\":[{\"type\":\"web_search\"}]}";
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", Raw(body))).StatusCode);
        Assert.Contains("web_search", _app.Upstream.Requests.Single().Body);

        var row = (await GateFactory.JsonAsync(await panel.GetAsync("/api/me/usage"))).GetProperty("recent")[0];
        Assert.Equal(2, row.GetProperty("webSearches").GetInt64());
        Assert.Equal(1.0, row.GetProperty("costUsd").GetDouble(), 6);
    }

    [Fact]
    public async Task Hides_the_owners_organization_in_errors_headers_and_model_lists()
    {
        var (codex, _, _) = await SetupAsync();
        _app.Upstream.Responder = (req, _) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return Task.FromResult(FakeUpstream.Json(HttpStatusCode.OK,
                    "{\"data\":[{\"id\":\"gpt-5.5\",\"owned_by\":\"org-SECRETORG1\"},{\"id\":\"ft:gpt-5.5:org-SECRETORG1::abc\"}]}"));
            }
            var r = FakeUpstream.Json(HttpStatusCode.TooManyRequests,
                "{\"error\":{\"message\":\"Rate limit reached for gpt-5.5 in organization org-SECRETORG1 (proj_SECRETPROJ1) on tokens per min\"}}");
            r.Headers.Add("x-ratelimit-remaining-tokens", "1234");
            r.Headers.Add("x-codex-primary-used-percent", "1");
            r.Headers.Add("Retry-After", "3");
            return Task.FromResult(r);
        };

        var res = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains("Rate limit reached", text);
        Assert.DoesNotContain("SECRETORG1", text);
        Assert.DoesNotContain("SECRETPROJ1", text);
        Assert.False(res.Headers.Contains("x-ratelimit-remaining-tokens"));
        Assert.Equal("3", res.Headers.GetValues("Retry-After").Single());

        var models = await codex.GetStringAsync("/v1/models");
        Assert.DoesNotContain("SECRETORG1", models);
        Assert.Contains("gpt-5.5", models);
    }

    [Fact]
    public async Task A_connect_timeout_fails_over_and_does_not_leak_the_busy_counter()
    {
        var (codex, _, admin) = await SetupAsync();
        _app.Upstream.Responder = (req, _) => req.RequestUri!.Host == "up1.test"
            ? throw new TaskCanceledException("connect timed out")
            : Task.FromResult(FakeUpstream.Sse(FakeUpstream.Completed(5, 0, 1)));

        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
        var up1 = (await GateFactory.JsonAsync(await admin.GetAsync("/api/admin/overview"))).GetProperty("upstreams")
            .EnumerateArray().First(u => u.GetProperty("name").GetString() == "primary");
        Assert.Equal("Timeout", up1.GetProperty("lastError").GetString());
        Assert.Equal(0, up1.GetProperty("inFlight").GetInt32());
    }

    [Fact]
    public async Task Requests_in_flight_count_against_the_quota()
    {
        var (codex, _, _) = await SetupAsync(new
        {
            name = "Tight",
            maxConcurrency = 3,
            limits = new[] { new { windowHours = 5, kind = "tokens", amount = 300 } },
        });
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(FakeUpstream.Completed(150, 0, 0)));
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode); // 150 used

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = Encoding.UTF8.GetBytes(FakeUpstream.Completed(150, 0, 0));
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(new HeldStream(full[..10], release, full[10..])));
        var running = await codex.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = GateFactory.ResponsesBody() },
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, running.StatusCode);

        // 150 used + one request running at the 150 average = 300: full, though nothing new is recorded yet.
        var refused = await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        release.SetResult();
        await running.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task When_Codex_hangs_up_just_before_the_end_the_real_usage_is_still_recorded()
    {
        var (codex, panel, _) = await SetupAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var full = Encoding.UTF8.GetBytes(FakeUpstream.Completed(500, 100, 50));
        var head = full[..60];
        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Sse(new HeldStream(head, release, full[60..])));

        using var cts = new CancellationTokenSource();
        var res = await codex.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = GateFactory.ResponsesBody() },
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await res.Content.ReadAsStreamAsync();
        await stream.ReadExactlyAsync(new byte[head.Length]);
        await cts.CancelAsync();
        res.Dispose();
        await Task.Delay(150);
        release.SetResult(); // the upstream finishes inside the drain window

        JsonElement row = default;
        for (var i = 0; i < 50 && row.ValueKind != JsonValueKind.Object; i++)
        {
            var recent = (await GateFactory.JsonAsync(await panel.GetAsync("/api/me/usage"))).GetProperty("recent");
            if (recent.GetArrayLength() > 0)
            {
                row = recent[0];
            }
            else
            {
                await Task.Delay(100);
            }
        }
        Assert.False(row.GetProperty("estimated").GetBoolean());
        Assert.Equal(500, row.GetProperty("inputTokens").GetInt64());
        Assert.Equal(50, row.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task Forwards_only_codex_headers()
    {
        var (codex, _, _) = await SetupAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = GateFactory.ResponsesBody() };
        request.Headers.Add("x-codex-turn-metadata", "{\"turn\":1}");
        request.Headers.Add("session-id", "s-1");
        request.Headers.Add("X-Forwarded-For", "6.6.6.6");
        request.Headers.Add("OpenAI-Project", "proj_other");
        request.Headers.Add("X-Something-Else", "x");
        Assert.Equal(HttpStatusCode.OK, (await codex.SendAsync(request)).StatusCode);
        var sent = _app.Upstream.Requests.Single().Headers;
        Assert.Equal("{\"turn\":1}", sent["x-codex-turn-metadata"]);
        Assert.Equal("s-1", sent["session-id"]);
        Assert.False(sent.ContainsKey("X-Forwarded-For"));
        Assert.False(sent.ContainsKey("OpenAI-Project"));
        Assert.False(sent.ContainsKey("X-Something-Else"));
    }

    [Theory]
    [InlineData("https://codex.example.com/\"; rm -rf ~")]
    [InlineData("https://codex.example.com/a b")]
    [InlineData("https://user:pass@codex.example.com")]
    [InlineData("https://codex.example.com/$(whoami)")]
    public async Task The_public_address_cannot_carry_anything_into_setup_commands(string url)
    {
        var admin = await _app.AdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings", new { publicUrl = url })).StatusCode);
    }
}

public class LoginGuardTests
{
    [Fact]
    public void Locks_an_account_after_repeated_failures_for_a_while()
    {
        var clock = new ManualClock();
        var guard = new LoginGuard(clock);
        for (var i = 0; i < LoginGuard.MaxFailures - 1; i++)
        {
            guard.Failed(7);
        }
        Assert.False(guard.IsLocked(7));
        guard.Failed(7);
        Assert.True(guard.IsLocked(7));
        Assert.False(guard.IsLocked(8));
        clock.Advance(LoginGuard.Window + TimeSpan.FromSeconds(1));
        Assert.False(guard.IsLocked(7));
        guard.Failed(7);
        guard.Succeeded(7);
        Assert.False(guard.IsLocked(7));
    }
}

public class RequestPolicyTests
{
    [Fact]
    public void The_forwarded_body_is_exactly_what_was_checked()
    {
        var input = "{\"model\":\" gpt-5.5 \",\"stream\":true,\"input\":[{\"role\":\"user\",\"content\":\"hi\"}]}"u8.ToArray();
        var (ok, error) = RequestPolicy.Apply(input, new RequestPolicy.Options(true, false, "cg-x"));
        Assert.Null(error);
        var sent = JsonDocument.Parse(ok!.Body).RootElement;
        Assert.Equal("gpt-5.5", sent.GetProperty("model").GetString());
        Assert.Equal("hi", sent.GetProperty("input")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void Safety_ids_are_stable_per_user_and_secret()
    {
        Assert.Equal(RequestPolicy.SafetyIdentifier(1, "s"), RequestPolicy.SafetyIdentifier(1, "s"));
        Assert.NotEqual(RequestPolicy.SafetyIdentifier(1, "s"), RequestPolicy.SafetyIdentifier(2, "s"));
        Assert.NotEqual(RequestPolicy.SafetyIdentifier(1, "s"), RequestPolicy.SafetyIdentifier(1, "t"));
    }

    [Theory]
    [InlineData("Rate limit in organization org-AbCd1234 on proj_Zz99xx for key sk-proj-abcdef", "org-***", "proj_***", "sk-***")]
    public void Scrubs_account_ids(string text, string a, string b, string c)
    {
        var s = GatewayHandler.Scrub(text);
        Assert.Contains(a, s);
        Assert.Contains(b, s);
        Assert.Contains(c, s);
        Assert.DoesNotContain("AbCd1234", s);
    }
}
