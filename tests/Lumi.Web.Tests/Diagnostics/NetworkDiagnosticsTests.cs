using System.Net;
using System.Net.Sockets;
using System.Text;
using Lumi.Core.Web;
using Lumi.Web.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Web.Tests.Diagnostics;

public sealed class NetworkDiagnosticsTests
{
    /// <summary>永遠直接連線的 <see cref="IWebProxy"/>。</summary>
    private sealed class DirectProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => null;
        public bool IsBypassed(Uri host) => true;
    }

    private sealed class FakeProxyInfo(Uri? proxy = null) : INetworkProxyInfo
    {
        public ProxyDecision Decide(Uri target) => new(proxy is null ? ProxyMode.None : ProxyMode.Manual, proxy, null);
    }

    /// <summary>最小的本機 HTTP 伺服器：讀完請求標頭就回應，記錄收到的方法。</summary>
    private sealed class LocalHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, int> _statusFor;
        private readonly CancellationTokenSource _stop = new();

        public LocalHttpServer(Func<string, int> statusFor)
        {
            _statusFor = statusFor;
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Uri Url => new($"http://127.0.0.1:{Port}/");

        public List<string> Methods { get; } = [];

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var request = new StringBuilder();
                    while (!request.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                            return;
                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    var method = request.ToString().Split(' ')[0];
                    lock (Methods)
                        Methods.Add(method);
                    var status = _statusFor(method);
                    var response = $"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static NetworkDiagnostics Create(Uri? proxy = null) =>
        new(new DirectProxy(), new FakeProxyInfo(proxy), NullLogger<NetworkDiagnostics>.Instance);

    [Fact]
    public async Task Local_server_reports_dns_tcp_and_http()
    {
        using var server = new LocalHttpServer(_ => 200);
        var target = new Uri($"http://localhost:{server.Port}/");

        var result = await Create().CheckAsync(target, CancellationToken.None);

        Assert.Null(result.DnsError);
        Assert.NotEmpty(result.Addresses);
        Assert.Contains(result.Addresses, a => a is "127.0.0.1" or "::1");
        Assert.Equal($"localhost:{server.Port}", result.ConnectTarget);
        Assert.NotNull(result.ConnectMs);
        Assert.Null(result.ConnectError);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.HttpMs);
        Assert.Null(result.HttpError);
        Assert.Null(result.Certificate);
        Assert.Null(result.Proxy.Proxy);
    }

    [Fact]
    public async Task Ipv4_addresses_come_first_and_are_limited_to_five()
    {
        using var server = new LocalHttpServer(_ => 200);

        var result = await Create().CheckAsync(server.Url, CancellationToken.None);

        Assert.Equal(["127.0.0.1"], result.Addresses);
    }

    [Fact]
    public async Task Reports_http_status_codes_other_than_200()
    {
        using var server = new LocalHttpServer(_ => 404);

        var result = await Create().CheckAsync(server.Url, CancellationToken.None);

        Assert.Equal(404, result.StatusCode);
        Assert.Null(result.HttpError);
    }

    [Fact]
    public async Task Head_not_allowed_retries_with_get()
    {
        using var server = new LocalHttpServer(method => method == "HEAD" ? 405 : 200);

        var result = await Create().CheckAsync(server.Url, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        string[] methods;
        lock (server.Methods)
            methods = [.. server.Methods];
        Assert.Contains("HEAD", methods);
        Assert.Contains("GET", methods);
    }

    [Fact]
    public async Task Closed_port_reports_connect_and_http_errors()
    {
        var target = new Uri($"http://127.0.0.1:{FreePort()}/");

        var result = await Create().CheckAsync(target, CancellationToken.None);

        Assert.Null(result.DnsError);
        Assert.Null(result.ConnectMs);
        Assert.Equal("連不上", result.ConnectError);
        Assert.Null(result.StatusCode);
        Assert.Null(result.HttpMs);
        Assert.False(string.IsNullOrEmpty(result.HttpError));
    }

    [Fact]
    public async Task Unknown_host_reports_dns_error()
    {
        var result = await Create().CheckAsync(new Uri("http://no-such-host.invalid/"), CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(result.DnsError));
        Assert.Empty(result.Addresses);
        Assert.NotNull(result.ConnectError);
        Assert.NotNull(result.HttpError);
    }

    [Fact]
    public async Task Tcp_step_connects_to_the_proxy_when_there_is_one()
    {
        using var proxyServer = new LocalHttpServer(_ => 200);
        var proxyUri = new Uri($"http://127.0.0.1:{proxyServer.Port}");
        // 目標主機不存在：如果 TCP 連的是目標而不是 Proxy 就會失敗
        var target = new Uri("http://no-such-host.invalid/");

        var result = await Create(proxyUri).CheckAsync(target, CancellationToken.None);

        Assert.Equal($"127.0.0.1:{proxyServer.Port}", result.ConnectTarget);
        Assert.NotNull(result.ConnectMs);
        Assert.Null(result.ConnectError);
        Assert.Equal(proxyUri, result.Proxy.Proxy);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var server = new LocalHttpServer(_ => 200);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().CheckAsync(server.Url, cts.Token));
    }
}
