using System.Net;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Lumi.Web.Proxy;
using Xunit;

namespace Lumi.Web.Tests.Proxy;

public class ProxyStateTests
{
    private static readonly PacScriptLoader Loader = new();
    private static readonly FakeSystemProxy NoSystemProxy = new(_ => null);

    private static Task<ProxyState> Create(NetworkSettings settings, SecretValue? password = null, IWebProxy? system = null, PacScriptLoader? loader = null) =>
        ProxyState.CreateAsync(settings, password, loader ?? Loader, system ?? NoSystemProxy, CancellationToken.None);

    [Fact]
    public async Task None_is_always_direct()
    {
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.None });
        var d = state.Decide(new Uri("https://example.com/"));
        Assert.Null(d.Proxy);
        Assert.Equal(ProxyMode.None, d.Mode);
        Assert.Null(state.Credentials);
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST:8080/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    public async Task Local_addresses_are_always_direct(string url)
    {
        var settings = new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "proxy.example.com:8080" };
        using var state = await Create(settings);
        Assert.Null(state.Decide(new Uri(url)).Proxy);

        using var system = await Create(new NetworkSettings(), system: new FakeSystemProxy(_ => new Uri("http://sys:3128")));
        Assert.Null(system.Decide(new Uri(url)).Proxy);
    }

    [Fact]
    public async Task Manual_uses_proxy_except_bypass_list()
    {
        var settings = new NetworkSettings
        {
            Mode = ProxyMode.Manual,
            ManualProxy = "proxy.example.com:8080",
            BypassList = ["*.corp.example", "10.*"],
        };
        using var state = await Create(settings);

        var viaProxy = state.Decide(new Uri("https://www.example.org/x"));
        Assert.Equal("http://proxy.example.com:8080/", viaProxy.Proxy!.AbsoluteUri);
        Assert.Null(viaProxy.Problem);
        Assert.Null(state.Decide(new Uri("https://wiki.corp.example/")).Proxy);
        Assert.Null(state.Decide(new Uri("http://10.1.2.3/")).Proxy);
    }

    [Fact]
    public async Task Manual_with_unreadable_address_is_direct_with_problem()
    {
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "no-port" });
        var d = state.Decide(new Uri("https://example.com/"));
        Assert.Null(d.Proxy);
        Assert.NotNull(d.Problem);
    }

    [Fact]
    public async Task System_follows_the_system_proxy()
    {
        var system = new FakeSystemProxy(t => t.Host == "direct.example" ? null : new Uri("http://sys.example:3128"));
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.System }, system: system);

        Assert.Equal("http://sys.example:3128/", state.Decide(new Uri("https://a.example/")).Proxy!.AbsoluteUri);
        Assert.Null(state.Decide(new Uri("https://direct.example/")).Proxy);
    }

    [Fact]
    public async Task System_result_equal_to_target_means_direct()
    {
        var target = new Uri("https://a.example/");
        var system = new FakeSystemProxy(t => t);   // WebProxy 在不需要 Proxy 時會回傳目標本身
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.System }, system: system);
        Assert.Null(state.Decide(target).Proxy);
    }

    [Fact]
    public async Task Pac_decides_per_url()
    {
        using var file = new TempPacFile("""
            function FindProxyForURL(url, host) {
              if (dnsDomainIs(host, '.corp.example')) return 'DIRECT';
              return 'PROXY pac-proxy.example:3128; DIRECT';
            }
            """);
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = file.Url });

        Assert.Null(state.Problem);
        Assert.Equal("http://pac-proxy.example:3128/", state.Decide(new Uri("https://www.example.org/")).Proxy!.AbsoluteUri);
        Assert.Null(state.Decide(new Uri("https://wiki.corp.example/")).Proxy);
        Assert.Null(state.Decide(new Uri("http://localhost/")).Proxy);   // 本機一律直接，不問 PAC
    }

    [Fact]
    public async Task Pac_download_failure_is_direct_with_problem()
    {
        var missing = new Uri(Path.Combine(Path.GetTempPath(), $"lumi-missing-{Guid.NewGuid():N}.pac")).AbsoluteUri;
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = missing });

        Assert.StartsWith("PAC 檔下載失敗：", state.Problem);
        Assert.EndsWith("，目前直接連線。", state.Problem);
        var d = state.Decide(new Uri("https://example.com/"));
        Assert.Null(d.Proxy);
        Assert.Equal(state.Problem, d.Problem);
    }

    [Fact]
    public async Task Pac_syntax_error_is_direct_with_problem()
    {
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = "file:///x" },
            loader: new CountingPacLoader("function FindProxyForURL( {"));
        Assert.StartsWith("PAC 檔有錯誤：", state.Problem);
        Assert.Null(state.Decide(new Uri("https://example.com/")).Proxy);
    }

    [Fact]
    public async Task Pac_runtime_error_is_direct_with_problem_per_decision()
    {
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = "file:///x" },
            loader: new CountingPacLoader("function FindProxyForURL(url, host) { return nope(); }"));
        Assert.Null(state.Problem);
        var d = state.Decide(new Uri("https://example.com/"));
        Assert.Null(d.Proxy);
        Assert.StartsWith("PAC 檔有錯誤：", d.Problem);
    }

    [Fact]
    public async Task Pac_result_is_cached_per_scheme_and_host()
    {
        // 讓 PAC 自己計數並把次數放進回傳的 Proxy 名稱：快取命中時名稱不變，代表 PAC 沒有再執行。
        using var file = new TempPacFile("""
            var calls = 0;
            function FindProxyForURL(url, host) { calls++; return 'PROXY p' + calls + '.example:80'; }
            """);
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = file.Url });

        var first = state.Decide(new Uri("https://a.example/one"));
        var second = state.Decide(new Uri("https://a.example/two?x=1"));   // 同 scheme＋主機：快取
        var otherHost = state.Decide(new Uri("https://b.example/"));
        var otherScheme = state.Decide(new Uri("http://a.example/"));

        Assert.Equal("p1.example", first.Proxy!.Host);
        Assert.Equal("p1.example", second.Proxy!.Host);
        Assert.Equal("p2.example", otherHost.Proxy!.Host);
        Assert.Equal("p3.example", otherScheme.Proxy!.Host);
    }

    [Fact]
    public async Task Auth_none_has_no_credentials()
    {
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", Auth = ProxyAuthMode.None });
        Assert.Null(state.Credentials);
    }

    [Fact]
    public async Task Auth_integrated_uses_default_network_credentials()
    {
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", Auth = ProxyAuthMode.Integrated });
        Assert.Same(CredentialCache.DefaultNetworkCredentials, state.Credentials);
    }

    [Fact]
    public async Task Auth_account_builds_credentials_and_splits_domain()
    {
        var settings = new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", Auth = ProxyAuthMode.Account, ProxyUser = @"CORP\alice" };
        using var state = await Create(settings, new SecretValue("s3cret"));
        var cred = Assert.IsType<NetworkCredential>(state.Credentials);
        Assert.Equal("alice", cred.UserName);
        Assert.Equal("CORP", cred.Domain);
        Assert.Equal("s3cret", cred.Password);

        using var plain = await Create(settings with { ProxyUser = "bob" }, new SecretValue("pw"));
        var plainCred = Assert.IsType<NetworkCredential>(plain.Credentials);
        Assert.Equal("bob", plainCred.UserName);
        Assert.Equal("", plainCred.Domain);
    }

    [Fact]
    public async Task Auth_account_without_password_has_no_credentials()
    {
        using var state = await Create(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", Auth = ProxyAuthMode.Account, ProxyUser = "bob" });
        Assert.Null(state.Credentials);
    }
}
