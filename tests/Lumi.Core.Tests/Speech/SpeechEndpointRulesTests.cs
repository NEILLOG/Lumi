using Lumi.Core.Speech;

namespace Lumi.Core.Tests.Speech;

public class SpeechEndpointRulesTests
{
    [Theory]
    [InlineData("https://example.test/speech/transcribe")]
    [InlineData("https://EXAMPLE.test:8443/a/b")]
    [InlineData("  https://example.test/x  ")]
    [InlineData("http://localhost:8080/transcribe")]
    [InlineData("http://127.0.0.1/transcribe")]
    [InlineData("http://[::1]:8080/transcribe")]
    public void Accepted_endpoints_are_valid(string endpoint)
    {
        var check = SpeechEndpointRules.Validate(endpoint);

        Assert.True(check.IsValid, check.Reason);
        Assert.NotNull(check.Uri);
        Assert.Null(check.Reason);
    }

    [Theory]
    [InlineData("http://example.test/transcribe")]
    [InlineData("http://192.168.1.10/transcribe")]
    public void Plain_http_is_only_allowed_for_localhost(string endpoint)
    {
        var check = SpeechEndpointRules.Validate(endpoint);

        Assert.False(check.IsValid);
        Assert.Contains("https", check.Reason);
    }

    [Theory]
    [InlineData("ftp://example.test/x")]
    [InlineData("file:///etc/passwd")]
    public void Other_schemes_are_rejected(string endpoint)
    {
        Assert.False(SpeechEndpointRules.Validate(endpoint).IsValid);
    }

    [Theory]
    [InlineData("https://user:secret@example.test/x")]
    [InlineData("https://user@example.test/x")]
    public void Credentials_in_the_url_are_rejected(string endpoint)
    {
        var check = SpeechEndpointRules.Validate(endpoint);

        Assert.False(check.IsValid);
        Assert.Contains("帳號或密碼", check.Reason);
    }

    [Theory]
    [InlineData("https://example.test/x?token=1")]
    [InlineData("https://example.test/x?")]
    public void Query_strings_are_rejected(string endpoint)
    {
        var check = SpeechEndpointRules.Validate(endpoint);

        Assert.False(check.IsValid);
        Assert.Contains("查詢字串", check.Reason);
    }

    [Theory]
    [InlineData("https://example.test/x#frag")]
    [InlineData("https://example.test/x#")]
    public void Fragments_are_rejected(string endpoint)
    {
        var check = SpeechEndpointRules.Validate(endpoint);

        Assert.False(check.IsValid);
        Assert.Contains("錨點", check.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_endpoint_means_not_configured(string? endpoint)
    {
        var check = SpeechEndpointRules.Validate(endpoint);

        Assert.False(check.IsValid);
        Assert.Contains("尚未輸入", check.Reason);
    }

    [Theory]
    [InlineData("example.test/transcribe")]
    [InlineData("not a url")]
    public void Relative_urls_are_rejected(string endpoint)
    {
        var check = SpeechEndpointRules.Validate(endpoint);

        Assert.False(check.IsValid);
        Assert.Contains("完整的網址", check.Reason);
    }
}
