using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>價目表比對（plan 第 4 節）。表格都是測試內手寫的小表。</summary>
public sealed class PriceMatcherTests
{
    private static readonly ConnectionSettings OpenAi = new("c-openai", "gateway", ProviderFormat.OpenAICompatible, "https://gateway.example.com/v1");
    private static readonly ConnectionSettings Anthropic = new("c-anthropic", "anthropic", ProviderFormat.Anthropic, "https://api.anthropic.com");
    private static readonly ConnectionSettings Azure = new("c-azure", "azure", ProviderFormat.OpenAICompatible, "https://myres.openai.azure.com/openai/v1");
    private static readonly ConnectionSettings Local = new("c-local", "ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");

    private static PriceEntry Entry(string key, string? provider = null) =>
        new(key, provider, 1m, 2m, null, null, null, null);

    private static PriceTable Table(params PriceEntry[] entries) => new(entries, new DateOnly(2026, 10, 31));

    [Fact]
    public void Exact_IgnoresCase()
    {
        var table = Table(Entry("gpt-4o", "openai"));

        var match = PriceMatcher.Match(table, "GPT-4O", OpenAi);

        Assert.Equal(PriceMatchKind.Exact, match.Kind);
        Assert.Equal("gpt-4o", match.Entry?.Key);
    }

    [Fact]
    public void Normalized_StripsProviderPrefix()
    {
        var table = Table(Entry("claude-x", "anthropic"));

        var match = PriceMatcher.Match(table, "anthropic/claude-x", Anthropic);

        Assert.Equal(PriceMatchKind.Normalized, match.Kind);
        Assert.Equal("claude-x", match.Entry?.Key);
    }

    [Fact]
    public void Normalized_StripsDateSuffix()
    {
        var anthropicTable = Table(Entry("claude-sonnet-4", "anthropic"));
        var openAiTable = Table(Entry("gpt-4o", "openai"));

        Assert.Equal(PriceMatchKind.Normalized, PriceMatcher.Match(anthropicTable, "claude-sonnet-4-20250514", Anthropic).Kind);
        Assert.Equal(PriceMatchKind.Normalized, PriceMatcher.Match(openAiTable, "gpt-4o-2024-08-06", OpenAi).Kind);
    }

    [Fact]
    public void Normalized_StripsLatestAndBedrockRegionAndVersion()
    {
        var ollamaTable = Table(Entry("llama3"));
        var bedrockTable = Table(Entry("claude-x", "bedrock"));

        Assert.Equal(PriceMatchKind.Normalized, PriceMatcher.Match(ollamaTable, "llama3:latest", OpenAi).Kind);
        Assert.Equal(PriceMatchKind.Normalized, PriceMatcher.Match(bedrockTable, "us.claude-x-v1:0", OpenAi).Kind);
    }

    [Fact]
    public void Prefix_IsCandidateNotAutoApplied()
    {
        var table = Table(Entry("gpt-4o", "openai"), Entry("gpt-4", "openai"));

        var match = PriceMatcher.Match(table, "gpt-4o-prod", OpenAi);

        Assert.Equal(PriceMatchKind.Candidates, match.Kind);
        Assert.Null(match.Entry);
        Assert.Equal("gpt-4o", match.Candidates[0].Key);
    }

    [Fact]
    public void Fuzzy_ReturnsTopThree()
    {
        var table = Table(
            Entry("claude-sonnet-4", "anthropic"),
            Entry("claude-sonnet-3-7", "anthropic"),
            Entry("claude-haiku-4", "anthropic"),
            Entry("claude-opus-4", "anthropic"),
            Entry("zebra", "anthropic"));

        var match = PriceMatcher.Match(table, "claude-sonet-4", Anthropic);

        Assert.Equal(PriceMatchKind.Candidates, match.Kind);
        Assert.Null(match.Entry);
        Assert.Equal(3, match.Candidates.Count);
        Assert.Equal("claude-sonnet-4", match.Candidates[0].Key);
        Assert.DoesNotContain(match.Candidates, e => e.Key == "zebra");
    }

    [Fact]
    public void Fuzzy_DoesNotAutoApply_MiniVsFull()
    {
        var table = Table(Entry("gpt-4.1", "openai"), Entry("gpt-4.1-mini", "openai"));

        var match = PriceMatcher.Match(table, "gpt-4.1-mimi", OpenAi);

        Assert.Equal(PriceMatchKind.Candidates, match.Kind);
        Assert.Null(match.Entry);
        Assert.Equal(new[] { "gpt-4.1", "gpt-4.1-mini" }, match.Candidates.Select(e => e.Key));
    }

    [Fact]
    public void Candidates_PrefixFirst_ThenFuzzy_DeduplicatedAndCapped()
    {
        var table = Table(
            Entry("claude-sonnet-4", "anthropic"),
            Entry("claude-sonnet-4-5", "anthropic"),
            Entry("claude-sonnet-3-7", "anthropic"),
            Entry("claude-sonnet-4-thinking", "anthropic"));

        // 前綴只命中 claude-sonnet-4；近似的第一名是 claude-sonnet-4-5，而 claude-sonnet-4 會被去重
        var match = PriceMatcher.Match(table, "claude-sonnet-4-x", Anthropic);

        Assert.Equal(PriceMatchKind.Candidates, match.Kind);
        Assert.Equal(3, match.Candidates.Count);
        Assert.Equal("claude-sonnet-4", match.Candidates[0].Key);
        Assert.Equal("claude-sonnet-4-5", match.Candidates[1].Key);
        Assert.Equal(3, match.Candidates.Select(e => e.Key.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Normalized_StripsBedrockVendorPrefix()
    {
        var table = Table(Entry("claude-x", "bedrock"));

        Assert.Equal(PriceMatchKind.Normalized, PriceMatcher.Match(table, "us.anthropic.claude-x-v1:0", OpenAi).Kind);
        Assert.Equal(PriceMatchKind.Normalized, PriceMatcher.Match(table, "anthropic.claude-x-20250101-v1:0", OpenAi).Kind);
    }

    [Fact]
    public void IntranetGateway_IsNotLocal_SoItIsPriced()
    {
        var table = Table(Entry("llama3"));
        var gateway = new ConnectionSettings("c-gw", "gw", ProviderFormat.OpenAICompatible, "http://10.0.0.5:8000/v1");

        var match = PriceMatcher.Match(table, "llama3", gateway);

        Assert.Equal(PriceMatchKind.Exact, match.Kind);
    }

    [Fact]
    public void Azure_PrefersAzureEntries()
    {
        var table = Table(Entry("azure/gpt-4o", "azure"), Entry("gpt-4o", "openai"));

        var azureMatch = PriceMatcher.Match(table, "gpt-4o-2024-08-06", Azure);
        var openAiMatch = PriceMatcher.Match(table, "gpt-4o-2024-08-06", OpenAi);

        Assert.Equal(PriceMatchKind.Normalized, azureMatch.Kind);
        Assert.Equal("azure", azureMatch.Entry?.Provider);
        Assert.Equal("openai", openAiMatch.Entry?.Provider);
    }

    [Fact]
    public void LocalhostConnection_IsMatchedLikeAnyOther()
    {
        // 網址分不出地端與雲端（Gateway 後面兩種都有）：不再有「本機」這個結果，要免費由使用者自己勾選
        var table = Table(Entry("llama3"));

        var match = PriceMatcher.Match(table, "llama3", Local);

        Assert.Equal(PriceMatchKind.Exact, match.Kind);
    }

    [Fact]
    public void NotFound_WhenNothingClose()
    {
        var table = Table(Entry("gpt-4o", "openai"), Entry("claude-x", "anthropic"));

        var match = PriceMatcher.Match(table, "zzzzzz", OpenAi);

        Assert.Equal(PriceMatchKind.NotFound, match.Kind);
        Assert.Empty(match.Candidates);
    }

    [Theory]
    [InlineData("anthropic/claude-x", "claude-x")]
    [InlineData("openrouter/anthropic/claude-x", "claude-x")]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4")]
    [InlineData("gpt-4o-2024-08-06", "gpt-4o")]
    [InlineData("llama3:latest", "llama3")]
    [InlineData("us.claude-x-v1:0", "claude-x")]
    [InlineData("us.anthropic.claude-x-v1:0", "claude-x")]
    [InlineData("anthropic.claude-x-20250101-v1:0", "claude-x")]
    public void Normalize_StripsAsPlanned(string key, string expected)
    {
        Assert.Equal(expected, PriceMatcher.Normalize(key));
    }

    [Fact]
    public void PreferredProvider_FollowsHostAndFormat()
    {
        Assert.Equal("azure", PriceMatcher.PreferredProvider(Azure));
        Assert.Equal("anthropic", PriceMatcher.PreferredProvider(Anthropic));
        Assert.Equal("openai", PriceMatcher.PreferredProvider(OpenAi));
    }

    [Fact]
    public void ToPricing_IsSuggested_AndNotFree()
    {
        var pricing = PriceMatcher.ToPricing(new PriceEntry("claude-x", "anthropic", 3m, 15m, 0.3m, 3.75m, null, null));

        Assert.Equal(new ModelPricing(3m, 15m, 0.3m, 3.75m, IsFree: false, Source: PriceSource.Suggested), pricing);
    }
}
