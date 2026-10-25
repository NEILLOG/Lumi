namespace Lumi.Core.Web;

/// <summary>
/// 判斷網址是不是搜尋引擎的搜尋頁（M17 D3）。Lumi 不提供網路搜尋，這種網址每次都要使用者確認。
/// 純函式，只看主機與路徑，不連線。
/// </summary>
public static class SearchEngineUrls
{
    public static bool IsSearch(Uri url)
    {
        var host = url.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host["www.".Length..];
        var path = url.AbsolutePath;

        // google.com、google.com.tw…（maps.google.com、docs.google.com 的主機不是以 google. 開頭，不算）
        if (host.StartsWith("google.", StringComparison.Ordinal) || host.StartsWith("yandex.", StringComparison.Ordinal))
            return PathStartsWith(path, "/search");

        switch (host)
        {
            case "bing.com" or "cn.bing.com":
            case "search.yahoo.com" or "search.yahoo.co.jp" or "tw.search.yahoo.com":
                return PathStartsWith(path, "/search");
            case "duckduckgo.com" or "html.duckduckgo.com" or "lite.duckduckgo.com":
                return HasQueryKey(url.Query, "q");
            case "baidu.com":
                return path.Equals("/s", StringComparison.OrdinalIgnoreCase);
            case "search.brave.com" or "ecosia.org" or "startpage.com":
                return PathStartsWith(path, "/search") || PathStartsWith(path, "/do/search");
            default:
                return false;
        }
    }

    private static bool PathStartsWith(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool HasQueryKey(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=', 2)[0];
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase) && pair.Contains('='))
                return true;
        }
        return false;
    }
}
