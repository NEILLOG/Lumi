using System.Net;

namespace Lumi.Core.Usage;

/// <summary>
/// 判斷連線是不是本機（loopback）。只認 localhost、127.0.0.0/8、::1 與 0.0.0.0；內網位址（10.x、172.16–31、192.168.x）與沒有點的主機名稱
/// 不算本機，公司內網的 Gateway 照一般連線計費。網址無法解析時回 false。
/// </summary>
public static class LocalConnection
{
    public static bool IsLoopback(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (host.StartsWith('[') && host.EndsWith(']'))
            host = host[1..^1];

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var address))
            return false;
        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }
}
