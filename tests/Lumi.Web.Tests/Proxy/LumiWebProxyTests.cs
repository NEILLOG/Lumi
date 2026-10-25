using System.Net;
using Lumi.Core.Web;
using Lumi.Web.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumi.Web.Tests.Proxy;

public class LumiWebProxyTests
{
    private static readonly Uri Target = new("https://www.example.org/page");

    private static LumiWebProxy Create(FakeCatalog catalog, InMemorySecrets? secrets = null, IWebProxy? system = null, PacScriptLoader? loader = null) =>
        new(catalog, secrets ?? new InMemorySecrets(), NullLogger<LumiWebProxy>.Instance, system ?? new FakeSystemProxy(_ => null), loader);

    [Fact]
    public async Task Follows_the_initial_settings()
    {
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p1.example:8080" });
        using var proxy = Create(catalog);
        await proxy.InitializeAsync(CancellationToken.None);

        Assert.Equal("http://p1.example:8080/", proxy.GetProxy(Target)!.AbsoluteUri);
        Assert.False(proxy.IsBypassed(Target));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost/")));
    }

    [Fact]
    public async Task Settings_change_switches_to_new_result()
    {
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p1.example:8080" });
        using var proxy = Create(catalog);
        Assert.Equal("p1.example", proxy.GetProxy(Target)!.Host);

        catalog.SetNetwork(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p2.example:9090" });
        Assert.Equal("p2.example", proxy.GetProxy(Target)!.Host);

        catalog.SetNetwork(new NetworkSettings { Mode = ProxyMode.None });
        Assert.Null(proxy.GetProxy(Target));
        Assert.Equal(ProxyMode.None, proxy.Decide(Target).Mode);

        await proxy.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public void Change_in_a_list_counts_as_a_network_change()
    {
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", BypassList = ["a.example"] });
        using var proxy = Create(catalog);
        Assert.Null(proxy.GetProxy(new Uri("https://a.example/")));

        catalog.SetNetwork(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", BypassList = ["b.example"] });
        Assert.NotNull(proxy.GetProxy(new Uri("https://a.example/")));
        Assert.Null(proxy.GetProxy(new Uri("https://b.example/")));
    }

    [Fact]
    public async Task Unrelated_settings_change_does_not_rebuild()
    {
        var loader = new CountingPacLoader("function FindProxyForURL(u, h) { return 'PROXY pac.example:1'; }");
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = "http://pac.example/proxy.pac" });
        using var proxy = Create(catalog, loader: loader);
        await proxy.InitializeAsync(CancellationToken.None);
        Assert.Equal(1, loader.LoadCount);

        catalog.ChangeOther();
        catalog.ChangeOther();
        Assert.Equal("pac.example", proxy.GetProxy(Target)!.Host);
        Assert.Equal(1, loader.LoadCount);

        // 網路設定真的改了才重新載入
        catalog.SetNetwork(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = "http://pac.example/other.pac" });
        await proxy.InitializeAsync(CancellationToken.None);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task Deleting_the_pac_file_after_an_unrelated_change_keeps_the_old_result()
    {
        using var file = new TempPacFile("function FindProxyForURL(u, h) { return 'PROXY pac.example:1'; }");
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = file.Url });
        using var proxy = Create(catalog);
        await proxy.InitializeAsync(CancellationToken.None);
        Assert.Equal("pac.example", proxy.GetProxy(Target)!.Host);

        File.Delete(file.Path);
        catalog.ChangeOther();
        Assert.Equal("pac.example", proxy.GetProxy(new Uri("https://other.example/"))!.Host);
    }

    [Fact]
    public async Task Pac_failure_is_direct_and_reported_in_decision()
    {
        var missing = new Uri(Path.Combine(Path.GetTempPath(), $"lumi-missing-{Guid.NewGuid():N}.pac")).AbsoluteUri;
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = missing });
        using var proxy = Create(catalog);
        await proxy.InitializeAsync(CancellationToken.None);

        var decision = proxy.Decide(Target);
        Assert.Null(decision.Proxy);
        Assert.Equal(ProxyMode.Pac, decision.Mode);
        Assert.StartsWith("PAC 檔下載失敗：", decision.Problem);
    }

    [Fact]
    public async Task Account_auth_reads_the_stored_password()
    {
        var secrets = new InMemorySecrets();
        secrets.Items[NetworkSecrets.ProxyPassword.Name] = "pw123";
        var catalog = new FakeCatalog(new NetworkSettings
        {
            Mode = ProxyMode.Manual, ManualProxy = "p:1", Auth = ProxyAuthMode.Account, ProxyUser = @"CORP\alice",
        });
        using var proxy = Create(catalog, secrets);
        await proxy.InitializeAsync(CancellationToken.None);

        var cred = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("alice", cred.UserName);
        Assert.Equal("CORP", cred.Domain);
        Assert.Equal("pw123", cred.Password);
        Assert.Equal(1, secrets.GetCount);
    }

    [Fact]
    public async Task Other_auth_modes_do_not_read_the_password()
    {
        var secrets = new InMemorySecrets();
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", Auth = ProxyAuthMode.Integrated });
        using var proxy = Create(catalog, secrets);
        await proxy.InitializeAsync(CancellationToken.None);

        Assert.Same(CredentialCache.DefaultNetworkCredentials, proxy.Credentials);
        Assert.Equal(0, secrets.GetCount);
    }

    [Fact]
    public void Credentials_setter_is_ignored()
    {
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "p:1", Auth = ProxyAuthMode.None });
        using var proxy = Create(catalog);
        proxy.Credentials = new NetworkCredential("x", "y");
        Assert.Null(proxy.Credentials);
    }

    [Fact]
    public void Default_constructor_works_for_DI()
    {
        var catalog = new FakeCatalog(new NetworkSettings { Mode = ProxyMode.None });
        using var proxy = new LumiWebProxy(catalog, new InMemorySecrets(), NullLogger<LumiWebProxy>.Instance);
        Assert.Null(proxy.GetProxy(Target));
    }
}
