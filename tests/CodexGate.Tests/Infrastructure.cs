using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodexGate.Gateway;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CodexGate.Tests;

public sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Stands in for the OpenAI API: records what it was sent and answers as each test says.</summary>
public sealed class FakeUpstream : HttpMessageHandler
{
    public ConcurrentQueue<Recorded> Requests { get; } = new();

    public Func<HttpRequestMessage, string, Task<HttpResponseMessage>> Responder { get; set; } =
        (_, _) => Task.FromResult(Sse(Completed(100, 40, 20)));

    public sealed record Recorded(string Url, string? Authorization, Dictionary<string, string> Headers, string Body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new Recorded(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), headers, body));
        return await Responder(request, body);
    }

    public static string Completed(long input, long cached, long output, string id = "resp_1") =>
        "event: response.created\n" +
        $"data: {{\"type\":\"response.created\",\"response\":{{\"id\":\"{id}\",\"status\":\"in_progress\"}}}}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hel\"}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"delta\":\"lo\"}\n\n" +
        "event: response.completed\n" +
        $"data: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"{id}\",\"status\":\"completed\",\"usage\":{{\"input_tokens\":{input},\"input_tokens_details\":{{\"cached_tokens\":{cached}}},\"output_tokens\":{output},\"output_tokens_details\":{{\"reasoning_tokens\":5}},\"total_tokens\":{input + output}}}}}}}\n\n";

    public static HttpResponseMessage Sse(string text) => Sse(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    public static HttpResponseMessage Sse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.Add("x-request-id", "req_upstream");
        response.Headers.Add("OpenAI-Organization", "org-secret");
        response.Headers.Add("Set-Cookie", "upstream=1");
        return response;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A stream that sends a first chunk, then waits until released before ending.</summary>
public sealed class HeldStream(byte[] first, TaskCompletionSource release, byte[] rest) : Stream
{
    private int _stage;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        switch (_stage)
        {
            case 0:
                _stage = 1;
                first.CopyTo(buffer);
                return first.Length;
            case 1:
                await release.Task.WaitAsync(ct);
                _stage = 2;
                rest.CopyTo(buffer);
                return rest.Length;
            default:
                return 0;
        }
    }
}

public sealed class GateFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "admin-password-1";

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "codexgate-tests-" + Guid.NewGuid().ToString("N"));
    public FakeUpstream Upstream { get; } = new();
    public FakeRemoteHost Remote { get; } = new();
    public Action<IServiceCollection>? ExtraServices { get; set; }
    public ManualClock Clock { get; } = new();
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Gate:AdminUsername"] = "admin",
        ["Gate:AdminPassword"] = AdminPassword,
        ["Gate:DrainSecondsAfterAbort"] = "1",
        ["Gate:Remote:Enabled"] = "true",
        ["Gate:Upstreams:0:Name"] = "primary",
        ["Gate:Upstreams:0:BaseUrl"] = "https://up1.test/v1",
        ["Gate:Upstreams:0:ApiKey"] = "sk-upstream-one",
        ["Gate:Upstreams:1:Name"] = "backup",
        ["Gate:Upstreams:1:BaseUrl"] = "https://up2.test/v1/",
        ["Gate:Upstreams:1:ApiKey"] = "sk-upstream-two",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Gate:DataDir", DataDir);
        foreach (var (k, v) in Settings)
        {
            builder.UseSetting(k, v);
        }
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<CodexGate.Remote.IRemoteHost>(Remote);
            ExtraServices?.Invoke(services);
            services.AddHttpClient(GatewayHandler.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Upstream);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(DataDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public HttpClient Browser()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-CodexGate", "1");
        return client;
    }

    public async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = Browser();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return client;
    }

    public Task<HttpClient> AdminAsync() => SignInAsync("admin", AdminPassword);

    public static async Task<long> CreatePlanAsync(HttpClient admin, object plan)
    {
        var res = await admin.PostAsJsonAsync("/api/admin/plans", plan);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    public static async Task<long> CreateUserAsync(HttpClient admin, string username, long? planId, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["username"] = username, ["password"] = "user-password-1", ["planId"] = planId, ["expiresInDays"] = 30 };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
            {
                body[p.Name.Length > 0 ? char.ToLowerInvariant(p.Name[0]) + p.Name[1..] : p.Name] = p.GetValue(extra);
            }
        }
        var res = await admin.PostAsJsonAsync("/api/admin/users", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    /// <summary>Signs the user in and makes a gateway key, returning a client that sends it like Codex does.</summary>
    public async Task<(HttpClient Codex, HttpClient Panel, string Key)> CodexForAsync(string username)
    {
        var panel = await SignInAsync(username, "user-password-1");
        var res = await panel.PostAsJsonAsync("/api/me/keys", new { name = "laptop" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var key = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
        var codex = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        codex.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return (codex, panel, key);
    }

    public static StringContent ResponsesBody(string model = "gpt-5.5", object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["model"] = model, ["stream"] = true, ["input"] = "hi" };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
            {
                body[p.Name] = p.GetValue(extra);
            }
        }
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
}
