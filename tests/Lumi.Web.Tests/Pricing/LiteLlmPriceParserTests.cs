using System.Text;
using System.Text.Json;
using Lumi.Core.Usage;
using Lumi.Web.Pricing;

namespace Lumi.Web.Tests.Pricing;

public sealed class LiteLlmPriceParserTests
{
    private const string BundledJson = "Lumi.Web.Pricing.litellm-model-prices.json";
    private const string BundledDate = "Lumi.Web.Pricing.litellm-model-prices.date.txt";

    private static PriceTable Parse(string json) =>
        LiteLlmPriceParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)), new DateOnly(2026, 10, 31));

    [Fact]
    public void ConvertsPerTokenToPerMillion()
    {
        var table = Parse("""{"m": {"mode": "chat", "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015}}""");

        var entry = Assert.Single(table.Entries);
        Assert.Equal("m", entry.Key);
        Assert.Equal(3.0000m, entry.InputPerMTok);
        Assert.Equal(15.0000m, entry.OutputPerMTok);
        Assert.Null(entry.CacheReadPerMTok);
        Assert.Null(entry.CacheWritePerMTok);
    }

    [Fact]
    public void ReadsCacheProviderAndTokenLimits()
    {
        var table = Parse("""
            {"m": {"mode": "chat", "input_cost_per_token": 0.000003, "output_cost_per_token": 0.000015,
                   "cache_read_input_token_cost": 0.0000003, "cache_creation_input_token_cost": 0.00000375,
                   "litellm_provider": "anthropic", "max_input_tokens": 200000, "max_output_tokens": 64000}}
            """);

        var entry = Assert.Single(table.Entries);
        Assert.Equal(0.3000m, entry.CacheReadPerMTok);
        Assert.Equal(3.7500m, entry.CacheWritePerMTok);
        Assert.Equal("anthropic", entry.Provider);
        Assert.Equal(200_000, entry.MaxInputTokens);
        Assert.Equal(64_000, entry.MaxOutputTokens);
    }

    [Fact]
    public void SkipsSampleSpec_NonChat_AndMissingPrices()
    {
        var table = Parse("""
            {
              "sample_spec": {"mode": "chat", "input_cost_per_token": 0.1, "output_cost_per_token": 0.1},
              "emb": {"mode": "embedding", "input_cost_per_token": 0.1, "output_cost_per_token": 0.1},
              "noOutput": {"mode": "chat", "input_cost_per_token": 0.1},
              "noMode": {"input_cost_per_token": 0.000001, "output_cost_per_token": 0.000002}
            }
            """);

        var entry = Assert.Single(table.Entries);
        Assert.Equal("noMode", entry.Key);
    }

    [Fact]
    public void BrokenEntry_DoesNotAffectOthers()
    {
        var table = Parse("""
            {
              "broken": {"mode": "chat", "input_cost_per_token": "abc", "output_cost_per_token": 0.000001},
              "badCache": {"mode": "chat", "input_cost_per_token": 0.000001, "output_cost_per_token": 0.000001, "cache_read_input_token_cost": "x"},
              "good": {"mode": "chat", "input_cost_per_token": 0.000001, "output_cost_per_token": 0.000002}
            }
            """);

        var entry = Assert.Single(table.Entries);
        Assert.Equal("good", entry.Key);
    }

    [Fact]
    public void WholeFileNotAnObject_Throws()
    {
        Assert.Throws<JsonException>(() => Parse("[]"));
    }

    [Fact]
    public void BundledSnapshot_ParsesWithEnoughEntries()
    {
        var assembly = typeof(LiteLlmPriceParser).Assembly;
        using var json = assembly.GetManifestResourceStream(BundledJson);
        Assert.NotNull(json);
        using var dateStream = assembly.GetManifestResourceStream(BundledDate);
        Assert.NotNull(dateStream);
        using var reader = new StreamReader(dateStream);
        var date = DateOnly.ParseExact(reader.ReadToEnd().Trim(), "yyyy-MM-dd");

        var table = LiteLlmPriceParser.Parse(json, date);

        Assert.True(table.Entries.Count >= 500, $"只解析出 {table.Entries.Count} 筆");
        Assert.Equal(date, table.SnapshotDate);
    }
}
