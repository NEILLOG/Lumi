using System.Net;
using System.Net.Sockets;
using System.Text;
using Lumi.Core;
using Lumi.Core.Models;
using Lumi.Core.Web;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Settings;
using Lumi.Platform;
using Lumi.Providers;
using Lumi.Web;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lumi.App.Tests;

/// <summary>M17 D2：Lumi 所有的 HttpClient（這裡用工廠建立的一般用戶端）都經過設定的 Proxy。用本機的 TcpListener 當 Proxy 驗證。</summary>
public sealed class NetworkProxyCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-netcomp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLumiCore();
        builder.Services.AddLumiPlatform(o =>
        {
            o.KeychainService = "io.github.neillog.lumi.tests";
            o.DpapiFilePath = Path.Combine(_directory, "secrets.json");
        });
        builder.Services.AddLumiWeb();
        builder.Services.AddLumiProviders();
        builder.Services.AddLumiInfrastructure(o => o.DatabasePath = Path.Combine(_directory, "lumi.db"));
        builder.Services.Configure<Lumi.Infrastructure.Attachments.AttachmentOptions>(o => o.Directory = Path.Combine(_directory, "attachments"));
        builder.Services.Configure<ModelCatalogOptions>(o => o.SettingsPath = Path.Combine(_directory, "settings.json"));
        return builder.Build();
    }

    [SkippableFact]
    public async Task Http_clients_go_through_the_configured_manual_proxy()
    {
        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Lumi.Platform 只支援 Windows 與 macOS");
        using var host = BuildHost();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var requestLine = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buffer = new byte[1024];
            var read = await stream.ReadAsync(buffer);
            var text = Encoding.ASCII.GetString(buffer, 0, read);
            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            await stream.WriteAsync(reply);
            return text.Split("\r\n")[0];
        });

        var catalog = host.Services.GetRequiredService<IModelCatalog>();
        await catalog.SetNetworkAsync(NetworkSettings.Default with
        {
            Mode = ProxyMode.Manual,
            ManualProxy = $"127.0.0.1:{port}",
            Auth = ProxyAuthMode.None,
        }, null);

        var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("anything");
        using var response = await http.GetAsync("http://lumi-proxy-test.invalid/path", new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);

        Assert.Equal("GET http://lumi-proxy-test.invalid/path HTTP/1.1", await requestLine);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }
}
