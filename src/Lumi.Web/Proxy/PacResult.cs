namespace Lumi.Web.Proxy;

/// <summary>解讀 PAC 的 <c>FindProxyForURL</c> 回傳值（例如 <c>PROXY a:8080; DIRECT</c>）。</summary>
internal static class PacResult
{
    /// <summary>取第一個可用的項目；<c>DIRECT</c>、全部略過或空字串都回 null（直接連線）。</summary>
    public static Uri? FirstProxy(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;

        foreach (var raw in result.Split(';'))
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;

            var parts = item.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            var keyword = parts[0].ToUpperInvariant();
            if (keyword == "DIRECT") return null;
            if (parts.Length < 2) continue;

            var scheme = keyword switch
            {
                "PROXY" or "HTTP" => "http",
                "SOCKS" or "SOCKS5" => "socks5",
                "SOCKS4" => "socks4",
                _ => null,   // HTTPS 與看不懂的項目略過
            };
            if (scheme is null) continue;

            if (Uri.TryCreate($"{scheme}://{parts[1].Trim()}", UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.Host) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.UserInfo))
                return uri;
        }
        return null;
    }
}
