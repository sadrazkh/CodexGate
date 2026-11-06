using System.Text;
using CodexGate.Api;
using CodexGate.Data;
using CodexGate.Gateway;
using CodexGate.Security;

namespace CodexGate.Tests;

public class UsageSnifferTests
{
    private static UsageSniffer FeedInChunks(string text, int chunk)
    {
        var sniffer = new UsageSniffer(sse: true);
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var i = 0; i < bytes.Length; i += chunk)
        {
            sniffer.Feed(bytes.AsSpan(i, Math.Min(chunk, bytes.Length - i)));
        }
        sniffer.Complete();
        return sniffer;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(100_000)]
    public void Reads_usage_from_the_completed_event_whatever_the_chunking(int chunk)
    {
        var sniffer = FeedInChunks(FakeUpstream.Completed(1200, 1000, 300), chunk);
        Assert.Equal(new TokenUsage(1200, 1000, 300, 5), sniffer.Usage);
        Assert.Equal(2, sniffer.DeltaEvents);
        Assert.True(sniffer.SawTerminalEvent);
    }

    [Fact]
    public void Handles_crlf_line_endings_and_a_last_line_without_newline()
    {
        var text = FakeUpstream.Completed(10, 0, 4).Replace("\n", "\r\n").TrimEnd();
        var sniffer = FeedInChunks(text, 3);
        Assert.Equal(new TokenUsage(10, 0, 4, 5), sniffer.Usage);
    }

    [Theory]
    [InlineData("response.incomplete")]
    [InlineData("response.failed")]
    public void Reads_usage_from_incomplete_and_failed_events(string type)
    {
        var text = $"data: {{\"type\":\"{type}\",\"response\":{{\"usage\":{{\"input_tokens\":7,\"output_tokens\":2}}}}}}\n\n";
        Assert.Equal(new TokenUsage(7, 0, 2, 0), FeedInChunks(text, 5).Usage);
    }

    [Fact]
    public void A_stream_cut_short_has_no_usage_but_counts_deltas()
    {
        var text = "data: {\"type\":\"response.created\",\"response\":{}}\n\n" +
                   string.Concat(Enumerable.Repeat("data: {\"type\":\"response.output_text.delta\",\"delta\":\"x\"}\n\n", 9));
        var sniffer = FeedInChunks(text, 11);
        Assert.Null(sniffer.Usage);
        Assert.Equal(9, sniffer.DeltaEvents);
        Assert.False(sniffer.SawTerminalEvent);
    }

    [Fact]
    public void Ignores_garbage_and_comments()
    {
        var text = ": keep-alive\n\ndata: [DONE]\n\ndata: {not json\n\nevent: x\n\n" + FakeUpstream.Completed(3, 1, 2);
        Assert.Equal(new TokenUsage(3, 1, 2, 5), FeedInChunks(text, 4).Usage);
    }

    [Fact]
    public void Reads_a_plain_json_body()
    {
        var sniffer = new UsageSniffer(sse: false);
        var json = "{\"id\":\"resp_1\",\"usage\":{\"input_tokens\":50,\"input_tokens_details\":{\"cached_tokens\":20},\"output_tokens\":9}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        sniffer.Feed(bytes.AsSpan(0, 10));
        sniffer.Feed(bytes.AsSpan(10));
        sniffer.Complete();
        Assert.Equal(new TokenUsage(50, 20, 9, 0), sniffer.Usage);
    }

    [Fact]
    public void Reads_chat_style_usage_names()
    {
        var sniffer = new UsageSniffer(sse: false);
        sniffer.Feed("{\"usage\":{\"prompt_tokens\":8,\"completion_tokens\":3,\"prompt_tokens_details\":{\"cached_tokens\":2}}}"u8);
        sniffer.Complete();
        Assert.Equal(new TokenUsage(8, 2, 3, 0), sniffer.Usage);
    }
}

public class UsageMathTests
{
    [Fact]
    public void Billable_tokens_weigh_cached_input()
    {
        Assert.Equal(200 + 800 * 0.1 + 50, UsageMath.BillableTokens(new TokenUsage(1000, 800, 50, 0), 0.1), 6);
        Assert.Equal(1050, UsageMath.BillableTokens(new TokenUsage(1000, 800, 50, 0), 1), 6);
        // Cached can never exceed input, and negative numbers count as zero.
        Assert.Equal(10 * 0.5, UsageMath.BillableTokens(new TokenUsage(10, 99, -5, 0), 0.5), 6);
    }

    [Fact]
    public void Cost_uses_per_million_prices()
    {
        var price = new PriceRow { Model = "gpt-5.5", InputPerM = 2, CachedPerM = 0.2, OutputPerM = 10 };
        var cost = UsageMath.Cost(new TokenUsage(1_000_000, 500_000, 100_000, 0), price);
        Assert.Equal(0.5 * 2 + 0.5 * 0.2 + 0.1 * 10, cost!.Value, 9);
        Assert.Null(UsageMath.Cost(new TokenUsage(1, 0, 1, 0), null));
    }

    [Fact]
    public void Exact_price_beats_the_longest_pattern()
    {
        PriceRow[] prices =
        [
            new() { Model = "gpt-*", InputPerM = 1 },
            new() { Model = "gpt-5*", InputPerM = 2 },
            new() { Model = "GPT-5.5", InputPerM = 3 },
        ];
        Assert.Equal(3, UsageMath.FindPrice(prices, "gpt-5.5")!.InputPerM);
        Assert.Equal(2, UsageMath.FindPrice(prices, "gpt-5.4-mini")!.InputPerM);
        Assert.Equal(1, UsageMath.FindPrice(prices, "gpt-4o")!.InputPerM);
        Assert.Null(UsageMath.FindPrice(prices, "o3"));
    }
}

public class ModelPatternTests
{
    [Theory]
    [InlineData("gpt-5.5", "gpt-5.5", true)]
    [InlineData("gpt-5.5", "GPT-5.5", true)]
    [InlineData("gpt-5.5", "gpt-5.5-mini", false)]
    [InlineData("gpt-5*", "gpt-5.5-mini", true)]
    [InlineData("gpt-5*", "gpt-4o", false)]
    [InlineData("*", "anything", true)]
    public void Matches(string pattern, string model, bool expected) =>
        Assert.Equal(expected, ModelPatterns.Matches(pattern, model));

    [Fact]
    public void Empty_list_allows_everything() => Assert.True(ModelPatterns.Allowed([], "x"));

    [Fact]
    public void Parses_commas_spaces_and_newlines_without_duplicates() =>
        Assert.Equal(["gpt-5.5", "gpt-5*", "o3"], ModelPatterns.Parse("gpt-5.5, gpt-5*\no3  GPT-5.5"));

    [Theory]
    [InlineData("gpt-5.5", true)]
    [InlineData("gpt-5*", true)]
    [InlineData("openai/gpt-5:latest", true)]
    [InlineData("gp*t", false)]
    [InlineData("", false)]
    [InlineData("=HYPERLINK(1)", false)]
    [InlineData("a b", false)]
    public void Validates(string pattern, bool expected) => Assert.Equal(expected, ModelPatterns.IsValid(pattern));
}

public class SecretsTests
{
    [Fact]
    public void Passwords_verify_and_do_not_repeat()
    {
        var a = PasswordHasher.Hash("correct horse");
        var b = PasswordHasher.Hash("correct horse");
        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("correct horse", a));
        Assert.False(PasswordHasher.Verify("correct horsf", a));
        Assert.False(PasswordHasher.Verify("x", "garbage"));
        Assert.False(PasswordHasher.NeedsRehash(a));
        Assert.True(PasswordHasher.NeedsRehash("pbkdf2-sha256$1000$AAAA$AAAA"));
    }

    [Fact]
    public void Keys_are_random_prefixed_and_hashed()
    {
        var k1 = ApiKeys.Generate();
        var k2 = ApiKeys.Generate();
        Assert.StartsWith("cgk_", k1);
        Assert.NotEqual(k1, k2);
        Assert.True(ApiKeys.LooksLikeKey(k1));
        Assert.False(ApiKeys.LooksLikeKey("sk-123"));
        Assert.Equal(32, ApiKeys.HashOf(k1).Length);
        Assert.Equal(ApiKeys.HashOf(k1), ApiKeys.HashOf(k1));
        Assert.Equal(10, ApiKeys.DisplayPrefix(k1).Length);
    }

    [Fact]
    public void Generated_passwords_avoid_look_alikes()
    {
        var p = PasswordHasher.Generate(200);
        Assert.Equal(200, p.Length);
        Assert.DoesNotContain(p, c => "0O1lI".Contains(c));
    }
}

public class CsvTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-2", "'-2")]
    public void Cells_are_quoted_and_formulas_defused(string value, string expected) =>
        Assert.Equal(expected, AdminApi.CsvCell(value));
}

public class ValidationTests
{
    [Theory]
    [InlineData("ali", "ali")]
    [InlineData("  sara.k  ", "sara.k")]
    [InlineData("ab", null)]
    [InlineData("علی", null)]
    [InlineData("a b c", null)]
    public void Usernames(string input, string? expected) => Assert.Equal(expected, Validation.Username(input));
}
