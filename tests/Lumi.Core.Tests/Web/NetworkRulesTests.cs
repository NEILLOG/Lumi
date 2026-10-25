using Lumi.Core.Web;

namespace Lumi.Core.Tests.Web;

public class NetworkRulesTests
{
    [Theory]
    [InlineData("http://wpad.corp/proxy.pac")]
    [InlineData("https://corp.example/proxy.pac")]
    [InlineData("file:///C:/proxy.pac")]
    [InlineData("  http://wpad/proxy.pac  ")]
    public void Pac_url_accepts_http_https_file(string text) =>
        Assert.Null(NetworkRules.ValidatePacUrl(text));

    [Theory]
    [InlineData(null, "請輸入 PAC 網址")]
    [InlineData("   ", "請輸入 PAC 網址")]
    [InlineData("wpad/proxy.pac", "請輸入 http、https 或 file 開頭的完整網址")]
    [InlineData("ftp://wpad/proxy.pac", "請輸入 http、https 或 file 開頭的完整網址")]
    public void Pac_url_rejects_others(string? text, string error) =>
        Assert.Equal(error, NetworkRules.ValidatePacUrl(text));

    [Theory]
    [InlineData("proxy:8080", "http://proxy:8080/")]
    [InlineData("http://proxy:8080", "http://proxy:8080/")]
    [InlineData("http://proxy:8080/", "http://proxy:8080/")]
    [InlineData("socks5://p:1080", "socks5://p:1080/")]
    [InlineData("socks4://p:1080", "socks4://p:1080/")]
    [InlineData("proxy.example.com:65535", "http://proxy.example.com:65535/")]
    [InlineData("  proxy:3128  ", "http://proxy:3128/")]
    public void Manual_proxy_parses(string text, string expected)
    {
        Assert.True(NetworkRules.TryParseManualProxy(text, out var proxy));
        Assert.Equal(expected, proxy.AbsoluteUri);
        Assert.Null(NetworkRules.ValidateManualProxy(text));
    }

    [Theory]
    [InlineData("proxy")]                    // 沒有 port
    [InlineData("http://proxy")]             // 沒有明確的 port（預設 80 不算）
    [InlineData("proxy:0")]
    [InlineData("proxy:65536")]
    [InlineData("proxy:abc")]
    [InlineData("https://proxy:8080")]       // scheme 不支援
    [InlineData("ftp://proxy:21")]
    [InlineData("http://proxy:8080/path")]
    [InlineData("http://proxy:8080?x=1")]
    [InlineData("http://:8080")]
    [InlineData("a b:8080")]
    public void Manual_proxy_rejects(string text)
    {
        Assert.False(NetworkRules.TryParseManualProxy(text, out _));
        Assert.Equal("請輸入主機與連接埠，例如 proxy.example.com:8080", NetworkRules.ValidateManualProxy(text));
    }

    [Fact]
    public void Manual_proxy_blank_and_credentials()
    {
        Assert.Equal("請輸入 Proxy 位址", NetworkRules.ValidateManualProxy(null));
        Assert.Equal("請輸入 Proxy 位址", NetworkRules.ValidateManualProxy("  "));
        Assert.Equal("位址不可包含帳號密碼，請改用下方的驗證設定", NetworkRules.ValidateManualProxy("amy:secret@proxy:8080"));
        Assert.Equal("位址不可包含帳號密碼，請改用下方的驗證設定", NetworkRules.ValidateManualProxy("http://amy:secret@proxy:8080"));
        Assert.False(NetworkRules.TryParseManualProxy("http://amy:secret@proxy:8080", out _));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("proxy.corp.example.com")]
    [InlineData("localhost")]
    [InlineData("intranet")]
    [InlineData("*.example.com")]
    [InlineData("EXAMPLE.com")]
    [InlineData("10.0.0.5")]
    [InlineData("10.*")]
    [InlineData("192.168.*.*")]
    [InlineData("192.168.1.*")]
    [InlineData("*.*.*.*")]
    public void Host_pattern_accepts(string line) =>
        Assert.Null(NetworkRules.ValidateHostPattern(line));

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("http://example.com")]
    [InlineData("example.com:8080")]
    [InlineData("exa mple.com")]
    [InlineData("example..com")]
    [InlineData(".example.com")]
    [InlineData("*example.com")]
    [InlineData("foo.*.com")]
    [InlineData("**.example.com")]
    [InlineData("300.1.1.1")]
    [InlineData("10.1.1.1.1")]
    [InlineData("10.1")]
    public void Host_pattern_rejects(string line) =>
        Assert.Equal($"「{line}」不是有效的主機名稱", NetworkRules.ValidateHostPattern(line));

    [Fact]
    public void Host_patterns_return_first_error()
    {
        Assert.Null(NetworkRules.ValidateHostPatterns(["a.example", "*.b.example"]));
        Assert.Null(NetworkRules.ValidateHostPatterns([]));
        Assert.Equal("「x y」不是有效的主機名稱", NetworkRules.ValidateHostPatterns(["a.example", "x y", "http://z"]));
    }

    [Theory]
    [InlineData("example.com", "*.example.com", true)]           // 本身
    [InlineData("www.example.com", "*.example.com", true)]       // 子網域
    [InlineData("a.b.example.com", "*.example.com", true)]
    [InlineData("WWW.Example.COM", "*.example.com", true)]
    [InlineData("badexample.com", "*.example.com", false)]
    [InlineData("example.com.evil.net", "*.example.com", false)]
    [InlineData("example.com", "example.com", true)]
    [InlineData("www.example.com", "example.com", false)]
    [InlineData("10.1.2.3", "10.*", true)]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.6", "10.0.0.5", false)]
    [InlineData("192.168.1.20", "192.168.*.*", true)]
    [InlineData("192.168.1.20", "192.168.1.*", true)]
    [InlineData("192.168.2.20", "192.168.1.*", false)]
    [InlineData("192.168.1.20", "*.168.1.20", true)]    // 每段都是數字或 *，視為 IPv4 樣式（逐段比對）
    [InlineData("10.168.1.20", "*.168.1.21", false)]
    [InlineData("11.1.2.3", "10.*", false)]
    [InlineData("example.com", "10.*", false)]
    [InlineData("intranet", "intranet", true)]
    [InlineData("example.com", "", false)]
    public void Host_matches(string host, string pattern, bool expected) =>
        Assert.Equal(expected, NetworkRules.HostMatches(host, [pattern]));

    [Fact]
    public void Host_matches_any_pattern_in_list()
    {
        Assert.True(NetworkRules.HostMatches("a.corp", ["x.example", "a.corp"]));
        Assert.False(NetworkRules.HostMatches("a.corp", []));
    }

    [Fact]
    public void Proxy_user_and_test_url_and_timeout()
    {
        Assert.Null(NetworkRules.ValidateProxyUser(@"CORP\amy"));
        Assert.Equal("請輸入帳號", NetworkRules.ValidateProxyUser(null));
        Assert.Equal("請輸入帳號", NetworkRules.ValidateProxyUser("  "));

        Assert.Null(NetworkRules.ValidateTestUrl("https://example.com/"));
        Assert.Null(NetworkRules.ValidateTestUrl("http://10.0.0.1:8080/x"));
        Assert.Equal("請輸入 http 或 https 開頭的完整網址", NetworkRules.ValidateTestUrl(null));
        Assert.Equal("請輸入 http 或 https 開頭的完整網址", NetworkRules.ValidateTestUrl("example.com"));
        Assert.Equal("請輸入 http 或 https 開頭的完整網址", NetworkRules.ValidateTestUrl("ftp://example.com"));

        Assert.Null(NetworkRules.ValidateTimeout("5"));
        Assert.Null(NetworkRules.ValidateTimeout(" 30 "));
        Assert.Null(NetworkRules.ValidateTimeout("120"));
        foreach (var bad in new[] { null, "", "abc", "4", "121", "-5", "3.5", "1e2" })
            Assert.Equal("請輸入 5 到 120 之間的整數", NetworkRules.ValidateTimeout(bad));
    }

    [Fact]
    public void Parse_lines_trims_and_drops_blank()
    {
        Assert.Equal(["a.example", "b.example", "c"], NetworkRules.ParseLines(" a.example \r\n\r\nb.example\n  \n c "));
        Assert.Empty(NetworkRules.ParseLines(null));
        Assert.Empty(NetworkRules.ParseLines(""));
    }

    [Fact]
    public void Validate_default_is_valid() =>
        Assert.Null(NetworkRules.Validate(NetworkSettings.Default));

    [Fact]
    public void Validate_checks_only_fields_for_the_mode()
    {
        // 模式不是 Pac／Manual 時，PacUrl、ManualProxy、BypassList 的內容不檢查
        var junk = NetworkSettings.Default with { PacUrl = "壞", ManualProxy = "壞", BypassList = ["壞 掉"] };
        Assert.Null(NetworkRules.Validate(junk with { Mode = ProxyMode.System }));
        Assert.Null(NetworkRules.Validate(junk with { Mode = ProxyMode.None }));

        Assert.Equal("請輸入 http、https 或 file 開頭的完整網址", NetworkRules.Validate(junk with { Mode = ProxyMode.Pac }));
        Assert.Equal("請輸入主機與連接埠，例如 proxy.example.com:8080", NetworkRules.Validate(junk with { Mode = ProxyMode.Manual }));

        // Pac 模式不檢查 Manual 欄位，Manual 模式不檢查 PAC 欄位
        Assert.Null(NetworkRules.Validate(NetworkSettings.Default with { Mode = ProxyMode.Pac, PacUrl = "http://wpad/p.pac", ManualProxy = "壞" }));
        Assert.Null(NetworkRules.Validate(NetworkSettings.Default with { Mode = ProxyMode.Manual, ManualProxy = "proxy:8080", PacUrl = "壞" }));
    }

    [Fact]
    public void Validate_manual_checks_proxy_then_bypass_list()
    {
        var ok = NetworkSettings.Default with { Mode = ProxyMode.Manual, ManualProxy = "proxy:8080" };

        Assert.Null(NetworkRules.Validate(ok with { BypassList = ["*.corp", "10.*"] }));
        Assert.Equal("「x y」不是有效的主機名稱", NetworkRules.Validate(ok with { BypassList = ["x y"] }));
    }

    [Fact]
    public void Validate_account_user_is_required_unless_mode_is_none()
    {
        var account = NetworkSettings.Default with { Auth = ProxyAuthMode.Account, ProxyUser = "" };

        Assert.Equal("請輸入帳號", NetworkRules.Validate(account with { Mode = ProxyMode.System }));
        Assert.Equal("請輸入帳號", NetworkRules.Validate(account with { Mode = ProxyMode.Pac, PacUrl = "http://wpad/p.pac" }));
        Assert.Null(NetworkRules.Validate(account with { Mode = ProxyMode.None }));
        Assert.Null(NetworkRules.Validate(account with { Mode = ProxyMode.System, ProxyUser = @"CORP\amy" }));
        Assert.Null(NetworkRules.Validate(NetworkSettings.Default with { Auth = ProxyAuthMode.Integrated, ProxyUser = "" }));
    }

    [Fact]
    public void Validate_checks_integrated_sites_and_timeout()
    {
        Assert.Null(NetworkRules.Validate(NetworkSettings.Default with { IntegratedAuthSites = ["intranet.corp", "*.corp"] }));
        Assert.Equal("「http://x」不是有效的主機名稱", NetworkRules.Validate(NetworkSettings.Default with { IntegratedAuthSites = ["http://x"] }));

        Assert.Equal("請輸入 5 到 120 之間的整數", NetworkRules.Validate(NetworkSettings.Default with { TimeoutSeconds = 4 }));
        Assert.Equal("請輸入 5 到 120 之間的整數", NetworkRules.Validate(NetworkSettings.Default with { TimeoutSeconds = 121 }));
        Assert.Null(NetworkRules.Validate(NetworkSettings.Default with { TimeoutSeconds = 120 }));
    }
}
