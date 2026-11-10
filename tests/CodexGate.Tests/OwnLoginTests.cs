using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CodexGate.Tests;

/// <summary>Plans where each user signs in with their own ChatGPT account, and devices with their own limits.</summary>
public sealed class OwnLoginTests : IDisposable
{
    private readonly GateFactory _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<(HttpClient Admin, HttpClient Panel, long UserId)> UserAsync(bool ownLogin, object? limits = null, string name = "sara")
    {
        var admin = await _app.AdminAsync();
        var planId = await GateFactory.CreatePlanAsync(admin, new
        {
            name = "Plan " + name, ownLogin, remoteEnabled = true, remoteMaxSessions = 5, limits = limits ?? Array.Empty<object>(),
        });
        var userId = await GateFactory.CreateUserAsync(admin, name, planId);
        return (admin, await _app.SignInAsync(name, "user-password-1"), userId);
    }

    private static async Task<long> DeviceAsync(HttpClient panel, string name, object? limits = null)
    {
        var res = await panel.PostAsJsonAsync("/api/me/devices", new { name, limits = limits ?? Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await GateFactory.JsonAsync(res)).GetProperty("id").GetInt64();
    }

    private static async Task<string> KeyAsync(HttpClient panel, long? deviceId)
    {
        var res = await panel.PostAsJsonAsync("/api/me/keys", new { name = "k", deviceId });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await GateFactory.JsonAsync(res)).GetProperty("key").GetString()!;
    }

    /// <summary>What Codex sends when it is signed in with ChatGPT and set up for the relay.</summary>
    private HttpClient ChatGptCodex(string? gatewayKey, string? chatGptToken = "chatgpt-access-token")
    {
        var client = _app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        if (chatGptToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", chatGptToken);
        }
        if (gatewayKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-CodexGate-Key", gatewayKey);
        }
        client.DefaultRequestHeaders.Add("chatgpt-account-id", "acc-123");
        client.DefaultRequestHeaders.Add("Cookie", "browser=1");
        return client;
    }

    private const string Body = "{\"model\":\"gpt-5.5\",\"stream\":true,\"store\":false,\"input\":[{\"role\":\"user\",\"content\":\"hi\"}],\"tools\":[{\"type\":\"web_search\"}]}";

    private static StringContent RawBody() => new(Body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task The_users_own_sign_in_is_relayed_unchanged_and_metered_for_the_device()
    {
        var (_, panel, _) = await UserAsync(ownLogin: true);
        var laptop = await DeviceAsync(panel, "Laptop");
        var key = await KeyAsync(panel, laptop);

        using var codex = ChatGptCodex(key);
        var res = await codex.PostAsync("/chatgpt/v1/responses", RawBody());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("response.completed", await res.Content.ReadAsStringAsync());

        var sent = Assert.Single(_app.Upstream.Requests);
        Assert.Equal("https://chatgpt.test/backend-api/codex/responses", sent.Url);
        Assert.Equal("Bearer chatgpt-access-token", sent.Authorization); // the user's sign-in, never the owner's key
        Assert.Equal("acc-123", sent.Headers["chatgpt-account-id"]);
        Assert.False(sent.Headers.ContainsKey("X-CodexGate-Key"));
        Assert.False(sent.Headers.ContainsKey("Cookie"));
        Assert.Equal(Body, sent.Body); // nothing rewritten: it is the user's own subscription

        var usage = await GateFactory.JsonAsync(await panel.GetAsync("/api/me/usage"));
        var row = usage.GetProperty("recent")[0];
        Assert.Equal("Laptop", row.GetProperty("device").GetString());
        Assert.Equal(84, row.GetProperty("billableTokens").GetDouble());
        var devices = await GateFactory.JsonAsync(await panel.GetAsync("/api/me/devices"));
        Assert.Equal(84, devices[0].GetProperty("tokens30").GetDouble());

        var me = await GateFactory.JsonAsync(await panel.GetAsync("/api/me/"));
        Assert.True(me.GetProperty("gateway").GetProperty("ownLogin").GetBoolean());
        Assert.EndsWith("/chatgpt/v1", me.GetProperty("gateway").GetProperty("baseUrl").GetString());
    }

    [Fact]
    public async Task ChatGpts_own_answers_go_back_as_they_came()
    {
        var (_, panel, _) = await UserAsync(ownLogin: true);
        var key = await KeyAsync(panel, null);
        _app.Upstream.Responder = (_, _) =>
        {
            var r = FakeUpstream.Json(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"token expired\"}}");
            r.Headers.Add("x-codex-primary-used-percent", "12");
            return Task.FromResult(r);
        };
        using var codex = ChatGptCodex(key);
        var res = await codex.PostAsync("/chatgpt/v1/responses", RawBody());
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode); // Codex refreshes its sign-in on this
        Assert.Contains("token expired", await res.Content.ReadAsStringAsync());
        Assert.Equal("12", res.Headers.GetValues("x-codex-primary-used-percent").Single());

        _app.Upstream.Responder = (_, _) => Task.FromResult(FakeUpstream.Json(HttpStatusCode.OK, "{\"models\":[]}"));
        var models = await codex.GetAsync("/chatgpt/v1/models?client_version=0.166.0");
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Equal("https://chatgpt.test/backend-api/codex/models?client_version=0.166.0", _app.Upstream.Requests.Last().Url);
    }

    [Fact]
    public async Task Each_kind_of_plan_has_its_own_door()
    {
        var (admin, ownPanel, _) = await UserAsync(ownLogin: true);
        var ownKey = await KeyAsync(ownPanel, null);
        using (var apiStyle = _app.CreateClient())
        {
            apiStyle.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownKey);
            var res = await apiStyle.PostAsync("/v1/responses", GateFactory.ResponsesBody());
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Contains("own_login_plan", await res.Content.ReadAsStringAsync()); // the owner's key is never spent on it
        }
        using (var noSignIn = ChatGptCodex(ownKey, chatGptToken: null))
        {
            var res = await noSignIn.PostAsync("/chatgpt/v1/responses", RawBody());
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Contains("chatgpt_login_required", await res.Content.ReadAsStringAsync());
        }
        using (var wrongKey = ChatGptCodex("cgk_not-a-real-key-at-all-000000"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKey.PostAsync("/chatgpt/v1/responses", RawBody())).StatusCode);
        }
        using (var sneaky = ChatGptCodex(ownKey))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await sneaky.GetAsync("/chatgpt/v1/..%2F..%2Fsettings")).StatusCode);
        }

        var planId = await GateFactory.CreatePlanAsync(admin, new { name = "API plan" });
        await GateFactory.CreateUserAsync(admin, "reza", planId);
        var reza = await _app.SignInAsync("reza", "user-password-1");
        using var rezaRelay = ChatGptCodex(await KeyAsync(reza, null));
        var refused = await rezaRelay.PostAsync("/chatgpt/v1/responses", RawBody());
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("own_login_not_enabled", await refused.Content.ReadAsStringAsync());
        Assert.Empty(_app.Upstream.Requests);
    }

    [Fact]
    public async Task A_device_that_reached_its_own_limit_stops_and_the_others_go_on()
    {
        var (_, panel, _) = await UserAsync(ownLogin: false);
        var phone = await DeviceAsync(panel, "Phone", new[] { new { windowHours = 5, kind = "tokens", amount = 50 } });
        var laptop = await DeviceAsync(panel, "Laptop");
        using var phoneCodex = _app.CreateClient();
        phoneCodex.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await KeyAsync(panel, phone));
        using var laptopCodex = _app.CreateClient();
        laptopCodex.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await KeyAsync(panel, laptop));

        Assert.Equal(HttpStatusCode.OK, (await phoneCodex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
        var stopped = await phoneCodex.PostAsync("/v1/responses", GateFactory.ResponsesBody());
        Assert.Equal(HttpStatusCode.TooManyRequests, stopped.StatusCode);
        var error = (await GateFactory.JsonAsync(stopped)).GetProperty("error");
        Assert.Equal("usage_limit_reached", error.GetProperty("type").GetString());
        Assert.Contains("\"Phone\"", error.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.OK, (await laptopCodex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);

        var devices = await GateFactory.JsonAsync(await panel.GetAsync("/api/me/devices"));
        var phoneView = devices.EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Phone");
        Assert.True(phoneView.GetProperty("quota")[0].GetProperty("exhausted").GetBoolean());
    }

    [Fact]
    public async Task Devices_belong_to_their_owner()
    {
        var (admin, panel, _) = await UserAsync(ownLogin: false);
        var phone = await DeviceAsync(panel, "Phone");
        Assert.Equal(HttpStatusCode.Conflict, (await panel.PostAsJsonAsync("/api/me/devices", new { name = "phone" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await panel.PostAsJsonAsync("/api/me/devices", new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await panel.PostAsJsonAsync("/api/me/devices",
            new { name = "x", limits = new[] { new { windowHours = 0, kind = "tokens", amount = 1 } } })).StatusCode);

        var planId = await GateFactory.CreatePlanAsync(admin, new { name = "Other", remoteEnabled = true });
        await GateFactory.CreateUserAsync(admin, "reza", planId);
        var reza = await _app.SignInAsync("reza", "user-password-1");
        Assert.Equal(HttpStatusCode.BadRequest, (await reza.PostAsJsonAsync("/api/me/keys", new { name = "k", deviceId = phone })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reza.PutAsJsonAsync($"/api/me/devices/{phone}", new { name = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await reza.DeleteAsync($"/api/me/devices/{phone}")).StatusCode);
        var stolen = await reza.PostAsJsonAsync("/api/remote/sessions", new { name = "W", dir = "p", program = "codex", deviceId = phone });
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        Assert.Equal(0, (await GateFactory.JsonAsync(await reza.GetAsync("/api/me/devices"))).GetArrayLength());

        // A device with a session cannot go; once the session is gone, deleting it revokes its keys.
        var session = await panel.PostAsJsonAsync("/api/remote/sessions", new { name = "W", dir = "p", program = "codex", deviceId = phone });
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var sessionId = (await GateFactory.JsonAsync(session)).GetProperty("id").GetInt64();
        var key = await KeyAsync(panel, phone);
        Assert.Equal(HttpStatusCode.Conflict, (await panel.DeleteAsync($"/api/me/devices/{phone}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await panel.DeleteAsync($"/api/remote/sessions/{sessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await panel.DeleteAsync($"/api/me/devices/{phone}")).StatusCode);
        using var codex = _app.CreateClient();
        codex.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await codex.PostAsync("/v1/responses", GateFactory.ResponsesBody())).StatusCode);
    }

    [Fact]
    public async Task Sessions_on_an_own_login_plan_are_set_up_for_the_relay_and_count_for_their_device()
    {
        var (_, panel, userId) = await UserAsync(ownLogin: true);
        var phone = await DeviceAsync(panel, "Phone");
        var res = await panel.PostAsJsonAsync("/api/remote/sessions", new { name = "Task A", dir = "a", program = "shell", deviceId = phone });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var provision = Assert.Single(_app.Remote.Provisions);
        Assert.Equal(userId, provision.UserId);
        Assert.True(provision.OwnLogin);
        Assert.Equal("http://127.0.0.1:8080/chatgpt/v1", provision.Url);

        using var codex = ChatGptCodex(_app.Remote.Starts.Single().Key);
        Assert.Equal(HttpStatusCode.OK, (await codex.PostAsync("/chatgpt/v1/responses", RawBody())).StatusCode);
        var list = await GateFactory.JsonAsync(await panel.GetAsync("/api/remote/"));
        Assert.True(list.GetProperty("ownLogin").GetBoolean());
        var session = list.GetProperty("sessions")[0];
        Assert.Equal(phone, session.GetProperty("deviceId").GetInt64());
        Assert.Equal(84, session.GetProperty("tokens").GetDouble());
        var devices = await GateFactory.JsonAsync(await panel.GetAsync("/api/me/devices"));
        Assert.Equal(84, devices[0].GetProperty("tokens30").GetDouble());
    }
}
