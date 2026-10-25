using Lumi.Core.Browser;
using Lumi.Core.Web;

namespace Lumi.Browser.Tests;

public class BrowserLaunchPlannerTests
{
    private static readonly InstalledBrowser Edge = new(BrowserKind.Edge, "/apps/msedge", "130.0");
    private static readonly InstalledBrowser Chrome = new(BrowserKind.Chrome, "/apps/chrome", null);

    private static BrowserLaunchSpec Plan(
        NetworkSettings? network = null, InstalledBrowser? browser = null, BrowserSettings? settings = null,
        string? password = null, bool forceVisible = false) =>
        BrowserLaunchPlanner.Plan(
            browser ?? Edge, settings ?? BrowserSettings.Default, network ?? NetworkSettings.Default,
            password, "/data/browser", forceVisible);

    [Fact]
    public void System_mode_passes_nothing()
    {
        var spec = Plan(new NetworkSettings { Mode = ProxyMode.System });
        Assert.Empty(spec.Args);
        Assert.Null(spec.Proxy);
        Assert.Null(spec.Warning);
        Assert.Equal("/apps/msedge", spec.ExecutablePath);
    }

    [Fact]
    public void None_mode_disables_proxy()
    {
        var spec = Plan(new NetworkSettings { Mode = ProxyMode.None });
        Assert.Equal(["--no-proxy-server"], spec.Args);
        Assert.Null(spec.Proxy);
    }

    [Theory]
    [InlineData("http://wpad.corp/proxy.pac")]
    [InlineData("https://corp.example/proxy.pac")]
    public void Pac_mode_passes_pac_url(string url)
    {
        var spec = Plan(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = url });
        Assert.Equal(["--proxy-pac-url=" + url], spec.Args);
        Assert.Null(spec.Proxy);
    }

    [Theory]
    [InlineData("file:///C:/proxy.pac")]
    [InlineData("ftp://wpad/proxy.pac")]
    [InlineData("wpad/proxy.pac")]
    [InlineData("")]
    public void Pac_mode_rejects_non_http_urls(string url)
    {
        var ex = Assert.Throws<BrowserException>(() => Plan(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = url }));
        Assert.Equal(BrowserFailureKind.ProxyNotSupported, ex.Kind);
        Assert.Equal("瀏覽器不支援 file:// 的 PAC 檔，請改用 http 或 https 開頭的 PAC 網址。", ex.Message);
    }

    [Fact]
    public void Manual_mode_builds_proxy_with_bypass()
    {
        var network = new NetworkSettings
        {
            Mode = ProxyMode.Manual,
            ManualProxy = "proxy.corp:8080",
            BypassList = ["*.corp", "10.*"],
        };
        var spec = Plan(network);
        Assert.Empty(spec.Args);
        Assert.Equal(new BrowserProxySpec("http://proxy.corp:8080", "*.corp,10.*", null, null), spec.Proxy);
    }

    [Fact]
    public void Manual_mode_without_bypass_has_null_bypass()
    {
        var spec = Plan(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "http://proxy:3128/" });
        Assert.Equal(new BrowserProxySpec("http://proxy:3128", null, null, null), spec.Proxy);
    }

    [Fact]
    public void Manual_mode_accepts_socks5()
    {
        var spec = Plan(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "socks5://proxy:1080" });
        Assert.Equal("socks5://proxy:1080", spec.Proxy!.Server);
    }

    [Fact]
    public void Manual_mode_with_account_carries_credentials()
    {
        var network = new NetworkSettings
        {
            Mode = ProxyMode.Manual, ManualProxy = "proxy:8080", Auth = ProxyAuthMode.Account, ProxyUser = @"CORP\amy",
        };
        var spec = Plan(network, password: "secret");
        Assert.Equal(@"CORP\amy", spec.Proxy!.Username);
        Assert.Equal("secret", spec.Proxy.Password);
        Assert.Null(spec.Warning);
    }

    [Theory]
    [InlineData(ProxyAuthMode.None)]
    [InlineData(ProxyAuthMode.Integrated)]
    public void Manual_mode_without_account_does_not_carry_credentials(ProxyAuthMode auth)
    {
        var network = new NetworkSettings
        {
            Mode = ProxyMode.Manual, ManualProxy = "proxy:8080", Auth = auth, ProxyUser = "amy",
        };
        var spec = Plan(network, password: "secret");
        Assert.Null(spec.Proxy!.Username);
        Assert.Null(spec.Proxy.Password);
    }

    [Fact]
    public void Manual_mode_account_with_blank_user_has_no_credentials()
    {
        var network = new NetworkSettings
        {
            Mode = ProxyMode.Manual, ManualProxy = "proxy:8080", Auth = ProxyAuthMode.Account, ProxyUser = "  ",
        };
        var spec = Plan(network, password: "secret");
        Assert.Null(spec.Proxy!.Username);
        Assert.Null(spec.Proxy.Password);
    }

    [Theory]
    [InlineData("")]
    [InlineData("proxy")]
    [InlineData("ftp://proxy:21")]
    [InlineData("http://user:pw@proxy:8080")]
    public void Manual_mode_rejects_bad_address(string address)
    {
        var ex = Assert.Throws<BrowserException>(
            () => Plan(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = address }));
        Assert.Equal(BrowserFailureKind.ProxyNotSupported, ex.Kind);
        Assert.Equal("Proxy 位址格式不正確，請到設定的「網路」頁檢查。", ex.Message);
    }

    [Theory]
    [InlineData(ProxyMode.System)]
    [InlineData(ProxyMode.Pac)]
    public void System_or_pac_with_account_warns(ProxyMode mode)
    {
        var network = new NetworkSettings
        {
            Mode = mode, PacUrl = "http://wpad/proxy.pac", Auth = ProxyAuthMode.Account, ProxyUser = "amy",
        };
        var spec = Plan(network, password: "secret");
        Assert.Equal(
            "瀏覽器使用系統或 PAC 的 Proxy 時，無法自動帶入帳號與密碼；如果 Proxy 要求登入，請到設定打開「顯示瀏覽器視窗」，在視窗裡輸入。",
            spec.Warning);
        Assert.Null(spec.Proxy);
    }

    [Theory]
    [InlineData(ProxyMode.System, ProxyAuthMode.Integrated)]
    [InlineData(ProxyMode.System, ProxyAuthMode.None)]
    [InlineData(ProxyMode.None, ProxyAuthMode.Account)]
    public void Other_combinations_do_not_warn(ProxyMode mode, ProxyAuthMode auth) =>
        Assert.Null(Plan(new NetworkSettings { Mode = mode, Auth = auth }).Warning);

    [Fact]
    public void Integrated_auth_sites_become_allowlist()
    {
        var spec = Plan(new NetworkSettings { IntegratedAuthSites = ["*.corp", "intranet"] });
        Assert.Equal(["--auth-server-allowlist=*.corp,intranet"], spec.Args);
    }

    [Fact]
    public void Allowlist_is_added_after_proxy_args()
    {
        var network = new NetworkSettings { Mode = ProxyMode.None, IntegratedAuthSites = ["*.corp"] };
        Assert.Equal(["--no-proxy-server", "--auth-server-allowlist=*.corp"], Plan(network).Args);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void Headless_follows_setting_and_force_visible(bool showWindow, bool forceVisible, bool headless)
    {
        var spec = Plan(settings: new BrowserSettings { ShowWindow = showWindow }, forceVisible: forceVisible);
        Assert.Equal(headless, spec.Headless);
    }

    [Fact]
    public void Edge_and_chrome_use_separate_profiles()
    {
        var edge = Plan(browser: Edge);
        var chrome = Plan(browser: Chrome);
        Assert.Equal(Path.Combine("/data/browser", "edge"), edge.UserDataDir);
        Assert.Equal(Path.Combine("/data/browser", "chrome"), chrome.UserDataDir);
        Assert.NotEqual(edge.UserDataDir, chrome.UserDataDir);
        Assert.Equal("/apps/chrome", chrome.ExecutablePath);
    }
}
