using System.Net;
using System.Net.Http;
using Lumi.Core.Web;

namespace Lumi.Web;

/// <summary>把 HttpClient 的例外轉成給使用者看的 <see cref="WebRequestException"/>（WebClient、NetworkTester、NetworkDiagnostics 共用）。</summary>
internal static class WebFailures
{
    public static WebRequestException From(Exception e, int timeoutSeconds) => e switch
    {
        WebRequestException w => w,
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
            new(WebFailureKind.NameResolution, "找不到這個主機名稱（DNS 查不到）。"),
        HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError, StatusCode: HttpStatusCode.ProxyAuthenticationRequired } =>
            ProxyAuthentication(),
        HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } =>
            new(WebFailureKind.Proxy, "無法透過 Proxy 連線。請到設定的「網路」頁檢查 Proxy 設定。"),
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            new(WebFailureKind.Tls, "安全連線（TLS）失敗：憑證不受信任或已過期。公司網路若有 TLS 檢查，請把公司的根憑證安裝到系統。"),
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
            new(WebFailureKind.Connection, "連不上伺服器。"),
        TaskCanceledException or TimeoutException =>
            new(WebFailureKind.Timeout, $"連線逾時（{timeoutSeconds} 秒）。"),
        _ => new(WebFailureKind.Other, "連線失敗。"),
    };

    public static WebRequestException ProxyAuthentication() =>
        new(WebFailureKind.ProxyAuthentication, "Proxy 要求驗證，但驗證沒有通過。請到設定的「網路」頁檢查 Proxy 驗證方式。");
}
