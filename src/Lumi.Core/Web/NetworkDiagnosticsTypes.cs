using Lumi.Core.Secrets;

namespace Lumi.Core.Web;

/// <param name="Proxy">null＝直接連線。</param>
/// <param name="Problem">例如「PAC 檔下載失敗：…，目前直接連線」；沒有問題時是 null。</param>
public sealed record ProxyDecision(ProxyMode Mode, Uri? Proxy, string? Problem);

/// <summary>連到某個網址會走哪個 Proxy（實作在 Lumi.Web 的 <c>LumiWebProxy</c>；M18 的瀏覽器也用它）。</summary>
public interface INetworkProxyInfo
{
    ProxyDecision Decide(Uri target);
}

public sealed record CertificateInfo(string Subject, string Issuer, DateTimeOffset NotAfter);

/// <summary>net_check 的結果。每一段失敗時，數值是 null、Error 是原因。</summary>
public sealed record NetworkCheckResult(
    Uri Target,
    ProxyDecision Proxy,
    IReadOnlyList<string> Addresses,
    string? DnsError,
    string ConnectTarget,
    long? ConnectMs,
    string? ConnectError,
    int? StatusCode,
    long? HttpMs,
    string? HttpError,
    CertificateInfo? Certificate);

public interface INetworkDiagnostics
{
    Task<NetworkCheckResult> CheckAsync(Uri target, CancellationToken ct);
}

/// <param name="Message">給使用者看的一句話。</param>
public sealed record NetworkTestResult(bool Success, string Message);

/// <summary>設定頁的「測試連線」：用尚未儲存的設定連到 <paramref name="target"/>。</summary>
public interface INetworkTester
{
    /// <param name="password">Auth=Account 時用；null＝用已儲存的密碼。</param>
    Task<NetworkTestResult> TestAsync(NetworkSettings draft, SecretValue? password, Uri target, CancellationToken ct);
}
