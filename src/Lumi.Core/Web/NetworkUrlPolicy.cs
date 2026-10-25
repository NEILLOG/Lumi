namespace Lumi.Core.Web;

/// <summary>網址檢查的結果（M17 D3、D4）。</summary>
public enum UrlCheck
{
    /// <summary>網址在對話中出現過，或使用者已允許過這個網站：照一般的權限規則。</summary>
    Known,

    /// <summary>網站沒有在對話中出現過：一定詢問。</summary>
    UnknownSite,

    /// <summary>搜尋引擎的搜尋頁：一定詢問，而且不能記住。</summary>
    SearchEngine,
}

/// <summary>網址來源規則（M17 D3、D4）：決定工具要連的網址要不要強制詢問使用者。</summary>
public static class NetworkUrlPolicy
{
    /// <param name="isSiteApproved">這則對話裡使用者已允許過的網站（<see cref="WebOrigin.Of"/> 的格式）。</param>
    public static UrlCheck Check(Uri url, UrlProvenance provenance, Func<string, bool> isSiteApproved)
    {
        // 搜尋引擎優先：使用者自己貼的 Google 搜尋網址也要問
        if (SearchEngineUrls.IsSearch(url))
            return UrlCheck.SearchEngine;

        return provenance.IsKnown(url) || isSiteApproved(WebOrigin.Of(url))
            ? UrlCheck.Known
            : UrlCheck.UnknownSite;
    }

    /// <summary>確認區顯示的警告；<see cref="UrlCheck.Known"/> 回空字串。</summary>
    public static string Warning(UrlCheck check, Uri url) => check switch
    {
        UrlCheck.UnknownSite => $"這個網站沒有在對話中出現過：{url.Host}。確認是你要 Lumi 連線的網站再允許。",
        UrlCheck.SearchEngine => "這是搜尋引擎的網址。Lumi 不支援網路搜尋：可能被擋或要求驗證、結果不穩定，查詢內容也會傳給該網站。",
        _ => "",
    };
}
