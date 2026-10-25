using System.Net;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Lumi.Web.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lumi.Web.Tests.Proxy;

public class NetworkTesterTests
{
    private static NetworkTester Create(InMemorySecrets? secrets = null) =>
        new(secrets ?? new InMemorySecrets(), NullLogger<NetworkTester>.Instance, new FakeSystemProxy(_ => null), null);

    [Fact]
    public async Task Direct_connection_succeeds_with_message_format()
    {
        using var server = new LocalHttpServer(200, "OK", "hello");
        var result = await Create().TestAsync(new NetworkSettings { Mode = ProxyMode.None }, null, server.Url, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Matches(@"^連線成功（HTTP 200，直接連線，\d+ 毫秒）。$", result.Message);
    }

    [Fact]
    public async Task Any_http_status_except_407_counts_as_connected()
    {
        using var server = new LocalHttpServer(404, "Not Found");
        var result = await Create().TestAsync(new NetworkSettings { Mode = ProxyMode.None }, null, server.Url, CancellationToken.None);

        Assert.True(result.Success);
        Assert.StartsWith("連線成功（HTTP 404，", result.Message);
    }

    [Fact]
    public async Task Manual_proxy_is_used_and_named_in_the_message()
    {
        using var proxy = new LocalHttpServer(200, "OK", "via proxy");
        var settings = new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = $"127.0.0.1:{proxy.Port}", Auth = ProxyAuthMode.None };
        var result = await Create().TestAsync(settings, null, new Uri("http://lumi-test.invalid/"), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains($"經過 Proxy 127.0.0.1:{proxy.Port}", result.Message);
    }

    [Fact]
    public async Task Proxy_returning_407_is_an_authentication_failure()
    {
        using var proxy = new LocalHttpServer(407, "Proxy Authentication Required");
        var settings = new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = $"127.0.0.1:{proxy.Port}", Auth = ProxyAuthMode.None };
        var result = await Create().TestAsync(settings, null, new Uri("http://lumi-test.invalid/"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(WebFailures.ProxyAuthentication().Message, result.Message);
    }

    [Fact]
    public async Task Missing_pac_file_fails_with_download_message()
    {
        var missing = new Uri(Path.Combine(Path.GetTempPath(), $"lumi-missing-{Guid.NewGuid():N}.pac")).AbsoluteUri;
        using var server = new LocalHttpServer(200, "OK");
        var result = await Create().TestAsync(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = missing }, null, server.Url, CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("PAC 檔下載失敗：", result.Message);
    }

    [Fact]
    public async Task Unreachable_pac_url_fails_with_download_message()
    {
        // 127.0.0.1 上沒有人在聽的埠
        var unused = new LocalHttpServer(200, "OK");
        var port = unused.Port;
        unused.Dispose();
        using var server = new LocalHttpServer(200, "OK");
        var result = await Create().TestAsync(new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = $"http://127.0.0.1:{port}/proxy.pac" }, null, server.Url, CancellationToken.None);

        Assert.False(result.Success);
        Assert.StartsWith("PAC 檔下載失敗：", result.Message);
    }

    [Fact]
    public async Task Connection_failure_gives_a_readable_message()
    {
        var unused = new LocalHttpServer(200, "OK");
        var url = unused.Url;
        unused.Dispose();
        var result = await Create().TestAsync(new NetworkSettings { Mode = ProxyMode.None }, null, url, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("連不上伺服器。", result.Message);
    }

    [Fact]
    public async Task Account_auth_uses_supplied_password_without_touching_the_store()
    {
        using var proxy = new LocalHttpServer(200, "OK");
        var secrets = new InMemorySecrets();
        var settings = new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = $"127.0.0.1:{proxy.Port}", Auth = ProxyAuthMode.Account, ProxyUser = "alice" };
        var result = await Create(secrets).TestAsync(settings, new SecretValue("typed"), new Uri("http://lumi-test.invalid/"), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, secrets.GetCount);
    }

    [Fact]
    public async Task Account_auth_without_supplied_password_reads_the_stored_one()
    {
        using var proxy = new LocalHttpServer(200, "OK");
        var secrets = new InMemorySecrets();
        secrets.Items[NetworkSecrets.ProxyPassword.Name] = "stored";
        var settings = new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = $"127.0.0.1:{proxy.Port}", Auth = ProxyAuthMode.Account, ProxyUser = "alice" };
        var result = await Create(secrets).TestAsync(settings, null, new Uri("http://lumi-test.invalid/"), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, secrets.GetCount);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var server = new LocalHttpServer(200, "OK");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create().TestAsync(new NetworkSettings { Mode = ProxyMode.None }, null, server.Url, cts.Token));
    }
}
