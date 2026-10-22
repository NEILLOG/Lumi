using Lumi.Core.Speech;

namespace Lumi.Core.Tests.Speech;

public class SpeechSettingsTests
{
    [Fact]
    public void Default_is_unconfigured_zh_with_120_seconds()
    {
        var defaults = SpeechSettings.Default;

        Assert.Equal("", defaults.Endpoint);
        Assert.Equal("zh", defaults.Language);
        Assert.Equal(120, defaults.TimeoutSeconds);
        Assert.Null(defaults.TokenHint);
        Assert.Null(defaults.TokenHost);
    }

    [Fact]
    public void Normalize_trims_text_fields()
    {
        var normalized = new SpeechSettings("  https://example.test/x  ", "  en ", 30, "  1234 ", "  example.test ").Normalize();

        Assert.Equal("https://example.test/x", normalized.Endpoint);
        Assert.Equal("en", normalized.Language);
        Assert.Equal("1234", normalized.TokenHint);
        Assert.Equal("example.test", normalized.TokenHost);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(4, 5)]
    [InlineData(5, 5)]
    [InlineData(120, 120)]
    [InlineData(600, 600)]
    [InlineData(601, 600)]
    [InlineData(-30, 5)]
    public void Timeout_is_clamped_to_5_through_600_seconds(int seconds, int expected)
    {
        Assert.Equal(expected, new SpeechSettings(TimeoutSeconds: seconds).Normalize().TimeoutSeconds);
    }

    [Fact]
    public void Missing_language_falls_back_to_zh_but_blank_stays_blank_so_it_is_not_sent()
    {
        Assert.Equal("zh", new SpeechSettings(Language: null!).Normalize().Language);
        Assert.Equal("", new SpeechSettings(Language: "   ").Normalize().Language);
    }

    [Fact]
    public void Blank_token_fields_become_null()
    {
        var normalized = new SpeechSettings(TokenHint: "  ", TokenHost: "").Normalize();

        Assert.Null(normalized.TokenHint);
        Assert.Null(normalized.TokenHost);
    }

    [Fact]
    public void Null_endpoint_becomes_empty()
    {
        Assert.Equal("", new SpeechSettings(Endpoint: null!).Normalize().Endpoint);
    }
}
