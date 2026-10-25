using System.Net;
using Lumi.Core.Web;

namespace Lumi.Web.Proxy;

/// <summary>
/// 暫代 Core 的 <c>NetworkRules</c>（包 17A，開發期間尚未合併）：簽章與行為照計畫 17A 步驟 7。
/// 協調者合併後，整個檔案刪除，呼叫處改成 <c>NetworkRules.TryParseManualProxy</c>／<c>HostMatches</c>／<c>ParseLines</c>。
/// </summary>
internal static class NetworkRulesStub
{
    public static bool TryParseManualProxy(string text, out Uri proxy)
    {
        proxy = null!;
        var s = text?.Trim() ?? "";
        if (s.Length == 0) return false;
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "socks5" or "socks4")) return false;
        if (string.IsNullOrEmpty(uri.Host)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.Port is < 1 or > 65535) return false;

        // port 一定要在原字串裡明確寫出來（不能靠 scheme 的預設值）
        var afterScheme = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var authority = afterScheme.Split('/', 2)[0];
        var portText = authority.StartsWith('[')
            ? (authority.Contains("]:", StringComparison.Ordinal) ? authority[(authority.LastIndexOf("]:", StringComparison.Ordinal) + 2)..] : "")
            : (authority.Contains(':', StringComparison.Ordinal) ? authority[(authority.LastIndexOf(':') + 1)..] : "");
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535) return false;

        proxy = uri;
        return true;
    }

    public static bool HostMatches(string host, IReadOnlyList<string> patterns)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.Trim().ToLowerInvariant();
        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim().ToLowerInvariant() ?? "";
            if (pattern.Length == 0) continue;

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[2..];
                if (host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal)) return true;
                continue;
            }

            if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && IsIpv4Pattern(pattern))
            {
                var h = host.Split('.');
                var p = pattern.Split('.');
                var ok = true;
                for (var i = 0; i < p.Length && ok; i++)
                    ok = p[i] == "*" || p[i] == h[i];
                if (ok && (p.Length == 4 || p[^1] == "*")) return true;
                continue;
            }

            if (pattern == host) return true;
        }
        return false;
    }

    public static IReadOnlyList<string> ParseLines(string? multiline) =>
        (multiline ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsIpv4Pattern(string pattern)
    {
        var parts = pattern.Split('.');
        return parts.Length is >= 1 and <= 4 && parts.All(p => p == "*" || (byte.TryParse(p, out _)));
    }
}
