using Lumi.Web.Proxy;
using Xunit;

namespace Lumi.Web.Tests.Proxy;

public class PacResultTests
{
    [Theory]
    [InlineData("DIRECT", null)]
    [InlineData("PROXY proxy.example.com:8080", "http://proxy.example.com:8080/")]
    [InlineData("proxy 10.0.0.1:3128", "http://10.0.0.1:3128/")]
    [InlineData("SOCKS s.example.com:1080", "socks5://s.example.com:1080/")]
    [InlineData("SOCKS5 s.example.com:1080", "socks5://s.example.com:1080/")]
    [InlineData("SOCKS4 s.example.com:1080", "socks4://s.example.com:1080/")]
    [InlineData("HTTP h.example.com:80", "http://h.example.com/")]
    public void Each_kind_of_item_is_parsed(string input, string? expected)
    {
        Assert.Equal(expected, PacResult.FirstProxy(input)?.AbsoluteUri);
    }

    [Fact]
    public void First_usable_item_is_taken()
    {
        Assert.Equal("http://b:8080/", PacResult.FirstProxy("BOGUS x:1; PROXY b:8080 ; PROXY c:9090")!.AbsoluteUri);
    }

    [Fact]
    public void Https_item_is_skipped()
    {
        Assert.Equal("http://b:8080/", PacResult.FirstProxy("HTTPS a:443; PROXY b:8080")!.AbsoluteUri);
        Assert.Null(PacResult.FirstProxy("HTTPS a:443"));
    }

    [Fact]
    public void Direct_after_unusable_item_means_direct()
    {
        Assert.Null(PacResult.FirstProxy("HTTPS a:443; DIRECT; PROXY b:8080"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;")]
    [InlineData("PROXY")]
    [InlineData("NONSENSE")]
    public void Empty_or_unreadable_is_direct(string input)
    {
        Assert.Null(PacResult.FirstProxy(input));
    }
}
