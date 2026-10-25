using System.Diagnostics;
using Lumi.Web.Proxy;
using Xunit;

namespace Lumi.Web.Tests.Proxy;

public class PacEngineTests
{
    private static PacEngine Create(string script, Func<string, string?>? dns = null, string myIp = "192.168.1.5") =>
        new(script, dns ?? (_ => null), () => myIp);

    private static string Run(string body, string url = "http://example.com/a?b=1", Func<string, string?>? dns = null, string myIp = "192.168.1.5")
    {
        using var engine = Create($"function FindProxyForURL(url, host) {{ {body} }}", dns, myIp);
        return engine.FindProxyForUrl(new Uri(url));
    }

    [Theory]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "abbc", false)]
    [InlineData("http://*/x.html", "http://h/x.html", true)]
    [InlineData("a.b", "aXb", false)]   // 點是字面字元
    public void shExpMatch_matches_whole_string(string pattern, string text, bool expected)
    {
        var result = Run($"return shExpMatch('{text}', '{pattern}') ? 'YES' : 'NO';");
        Assert.Equal(expected ? "YES" : "NO", result);
    }

    [Theory]
    [InlineData("www.example.com", ".example.com", true)]
    [InlineData("www.example.com", ".other.com", false)]
    public void dnsDomainIs_checks_suffix(string host, string domain, bool expected)
    {
        Assert.Equal(expected ? "YES" : "NO", Run($"return dnsDomainIs('{host}', '{domain}') ? 'YES' : 'NO';"));
    }

    [Fact]
    public void isPlainHostName_checks_for_dots()
    {
        Assert.Equal("PLAIN", Run("return isPlainHostName(host) ? 'PLAIN' : 'FQDN';", "http://intranet/"));
        Assert.Equal("FQDN", Run("return isPlainHostName(host) ? 'PLAIN' : 'FQDN';", "http://a.example.com/"));
    }

    [Fact]
    public void isInNet_matches_ipv4_by_mask()
    {
        const string script = "return isInNet(host, '10.0.0.0', '255.0.0.0') ? 'PROXY p:1' : 'DIRECT';";
        Assert.Equal("PROXY p:1", Run(script, "http://10.20.30.40/"));
        Assert.Equal("DIRECT", Run(script, "http://11.20.30.40/"));
        Assert.Equal("PROXY p:1", Run("return isInNet(host, '192.168.128.0', '255.255.192.0') ? 'PROXY p:1' : 'DIRECT';", "http://192.168.130.7/"));
        Assert.Equal("DIRECT", Run("return isInNet(host, '192.168.128.0', '255.255.192.0') ? 'PROXY p:1' : 'DIRECT';", "http://192.168.192.7/"));
    }

    [Fact]
    public void isInNet_resolves_host_names_through_dns()
    {
        const string script = "return isInNet(host, '10.0.0.0', '255.0.0.0') ? 'PROXY p:1' : 'DIRECT';";
        var asked = new List<string>();
        Assert.Equal("PROXY p:1", Run(script, "http://intranet.example.com/", h => { asked.Add(h); return "10.1.2.3"; }));
        Assert.Equal(["intranet.example.com"], asked);
        Assert.Equal("DIRECT", Run(script, "http://elsewhere.example.com/", _ => "8.8.8.8"));
        Assert.Equal("DIRECT", Run(script, "http://nx.example.com/", _ => null));   // 查不到回 false
    }

    [Fact]
    public void isResolvable_and_dnsResolve_use_the_provided_dns()
    {
        Assert.Equal("1.2.3.4", Run("return dnsResolve(host);", dns: _ => "1.2.3.4"));
        Assert.Equal("NO", Run("return isResolvable(host) ? 'YES' : 'NO';", dns: _ => null));
    }

    [Fact]
    public void myIpAddress_comes_from_the_provided_function()
    {
        Assert.Equal("172.16.0.9", Run("return myIpAddress();", myIp: "172.16.0.9"));
    }

    [Fact]
    public void dnsDomainLevels_and_localHostOrDomainIs_work()
    {
        Assert.Equal("2", Run("return String(dnsDomainLevels(host));", "http://a.b.example/"));
        Assert.Equal("YES", Run("return localHostOrDomainIs('www', 'www.example.com') ? 'YES' : 'NO';"));
        Assert.Equal("NO", Run("return localHostOrDomainIs('web', 'www.example.com') ? 'YES' : 'NO';"));
    }

    [Fact]
    public void Time_functions_return_false()
    {
        Assert.Equal("NO", Run("return (weekdayRange('MON','FRI') || dateRange(1) || timeRange(8, 18)) ? 'YES' : 'NO';"));
    }

    [Fact]
    public void Https_url_is_reduced_to_host_only_and_http_keeps_path()
    {
        Assert.Equal("https://secure.example.com/|secure.example.com",
            Run("return url + '|' + host;", "https://secure.example.com/private/path?token=abc#frag"));
        Assert.Equal("http://plain.example.com/p/q?x=1|plain.example.com",
            Run("return url + '|' + host;", "http://plain.example.com/p/q?x=1#frag"));
    }

    [Fact]
    public void Syntax_error_throws_PacException()
    {
        var e = Assert.Throws<PacException>(() => Create("function FindProxyForURL( {"));
        Assert.StartsWith("PAC 檔有錯誤：", e.Message);
    }

    [Fact]
    public void Runtime_error_throws_PacException()
    {
        using var engine = Create("function FindProxyForURL(url, host) { return undefinedFunction(); }");
        Assert.Throws<PacException>(() => engine.FindProxyForUrl(new Uri("http://a.example/")));
    }

    [Fact]
    public void Infinite_loop_is_stopped_within_a_few_seconds()
    {
        using var engine = Create("function FindProxyForURL(url, host) { while (true) { } }");
        var watch = Stopwatch.StartNew();
        Assert.Throws<PacException>(() => engine.FindProxyForUrl(new Uri("http://a.example/")));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"花了 {watch.Elapsed}");
    }
}
