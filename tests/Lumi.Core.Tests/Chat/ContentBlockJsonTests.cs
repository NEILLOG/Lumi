using System.Text.Json;
using Lumi.Core.Chat;

namespace Lumi.Core.Tests.Chat;

public class ContentBlockJsonTests
{
    private static ContentBlock RoundTrip(ContentBlock block)
    {
        var json = JsonSerializer.Serialize(block);
        return JsonSerializer.Deserialize<ContentBlock>(json)!;
    }

    [Fact]
    public void TextBlock_round_trips()
    {
        var block = new TextBlock("中文與 emoji ❤️‍🔥");

        Assert.Equal(block, RoundTrip(block));
    }

    [Fact]
    public void ThinkingBlock_round_trips_with_signature()
    {
        var block = new ThinkingBlock("先想一下…", "opaque-signature-base64==");

        Assert.Equal(block, RoundTrip(block));
    }

    [Fact]
    public void RedactedThinkingBlock_round_trips()
    {
        var block = new RedactedThinkingBlock("opaque-redacted-data");

        Assert.Equal(block, RoundTrip(block));
    }

    [Fact]
    public void ProviderBlock_round_trips_raw_json_as_string()
    {
        var block = new ProviderBlock("anthropic", "fallback", """{"from":{"model":"claude-opus-5-5"}}""");

        var result = RoundTrip(block);

        Assert.Equal(block, result);
        Assert.IsType<ProviderBlock>(result);
    }

    [Fact]
    public void Discriminator_selects_the_right_type_when_deserializing_a_list()
    {
        IReadOnlyList<ContentBlock> blocks =
        [
            new TextBlock("a"),
            new ThinkingBlock("b", "sig"),
            new RedactedThinkingBlock("c"),
        ];

        var json = JsonSerializer.Serialize(blocks);
        var result = JsonSerializer.Deserialize<List<ContentBlock>>(json)!;

        Assert.Collection(result,
            b => Assert.IsType<TextBlock>(b),
            b => Assert.IsType<ThinkingBlock>(b),
            b => Assert.IsType<RedactedThinkingBlock>(b));
    }

    [Fact]
    public void Unknown_discriminator_throws_instead_of_silently_losing_data()
    {
        var json = """{"type":"something_new","text":"x"}""";

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ContentBlock>(json));
    }
}
