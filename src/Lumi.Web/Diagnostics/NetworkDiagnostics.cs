using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;

namespace Lumi.Web.Diagnostics;

/// <summary>網路診斷（M17 net_check）：Proxy 決策、DNS、TCP、HTTP 與憑證各自量測，一段失敗不影響其他段。</summary>
internal sealed class NetworkDiagnostics(IWebProxy proxy, INetworkProxyInfo proxyInfo, ILogger<NetworkDiagnostics> logger) : INetworkDiagnostics
{
    private const int StepTimeoutSeconds = 5;
    private const int HttpTimeoutSeconds = 15;
    private const int MaxAddresses = 5;

    public async Task<NetworkCheckResult> CheckAsync(Uri target, CancellationToken ct)
    {
        var decision = proxyInfo.Decide(target);
        var (addresses, dnsError) = await ResolveAsync(target, ct).ConfigureAwait(false);

        var connectHost = decision.Proxy?.IdnHost ?? target.IdnHost;
        var connectPort = decision.Proxy?.Port ?? target.Port;
        var (connectMs, connectError) = await ConnectAsync(connectHost, connectPort, ct).ConfigureAwait(false);
        var http = await ProbeHttpAsync(target, ct).ConfigureAwait(false);

        logger.LogInformation("網路診斷 {Host}：DNS {Dns}、TCP {Tcp}、HTTP {Http}",
            target.Host, dnsError ?? "正常", connectError ?? "正常", http.Error ?? http.StatusCode?.ToString() ?? "未知");
        return new NetworkCheckResult(
            target, decision, addresses, dnsError,
            $"{connectHost}:{connectPort}", connectMs, connectError,
            http.StatusCode, http.Ms, http.Error, http.Certificate);
    }

    private static async Task<(IReadOnlyList<string> Addresses, string? Error)> ResolveAsync(Uri target, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(StepTimeoutSeconds));
        try
        {
            var found = await Dns.GetHostAddressesAsync(target.IdnHost, timeout.Token).ConfigureAwait(false);
            var addresses = found
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Select(a => a.ToString())
                .Take(MaxAddresses)
                .ToList();
            return addresses.Count > 0 ? (addresses, null) : ([], "查不到");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ([], "查詢逾時");
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return ([], "查不到");
        }
    }

    private static async Task<(long? Ms, string? Error)> ConnectAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(StepTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return (stopwatch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "逾時");
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return (null, "連不上");
        }
    }

    private async Task<(int? StatusCode, long? Ms, string? Error, CertificateInfo? Certificate)> ProbeHttpAsync(Uri target, CancellationToken ct)
    {
        CertificateInfo? certificate = null;
        using var handler = new SocketsHttpHandler
        {
            Proxy = proxy,
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is not null)
                    {
                        using var detail = new X509Certificate2(cert);
                        certificate = new CertificateInfo(detail.Subject, detail.Issuer, new DateTimeOffset(detail.NotAfter));
                    }
                    return errors == SslPolicyErrors.None;
                },
            },
        };
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(HttpTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await SendAsync(http, target, HttpMethod.Head, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                response.Dispose();
                using var retry = await SendAsync(http, target, HttpMethod.Get, timeout.Token).ConfigureAwait(false);
                return ((int)retry.StatusCode, stopwatch.ElapsedMilliseconds, null, certificate);
            }
            return ((int)response.StatusCode, stopwatch.ElapsedMilliseconds, null, certificate);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = e is OperationCanceledException ? new TimeoutException() : e;
            return (null, null, WebFailures.From(failure, HttpTimeoutSeconds).Message, certificate);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, Uri target, HttpMethod method, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, target);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; Lumi)");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }
}
