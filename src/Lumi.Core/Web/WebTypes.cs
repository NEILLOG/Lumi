namespace Lumi.Core.Web;

public static class WebLimits
{
    public const long PageMaxBytes = 5L * 1024 * 1024;
    public const long DocumentMaxBytes = 20L * 1024 * 1024;
    public const long DownloadMaxBytes = 200L * 1024 * 1024;
    public const int MaxRedirects = 5;
    public const int DefaultMaxChars = 8_000;
    public const int MinMaxChars = 500;
    public const int MaxChars = 16_000;
}

/// <param name="Method">大寫的 HTTP 方法。</param>
public sealed record WebRequest(Uri Url, string Method = "GET")
{
    /// <summary>呼叫端自訂的標頭（工具已檢查過名稱與值）；會蓋過預設的 Accept 等標頭。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public string? Body { get; init; }

    /// <summary>有 <see cref="Body"/> 時的 Content-Type；null＝<c>application/json</c>。</summary>
    public string? ContentType { get; init; }

    /// <summary>回應內容最多讀幾個位元組；超過就停止讀取並設 <see cref="WebResponse.Truncated"/>。</summary>
    public long MaxBytes { get; init; } = WebLimits.PageMaxBytes;
}

/// <param name="FinalUrl">轉址之後的網址。</param>
/// <param name="MediaType">Content-Type 的媒體類型（小寫，不含參數）；沒有時是 null。</param>
/// <param name="Charset">Content-Type 的 charset（小寫）；沒有時是 null。</param>
/// <param name="Headers">回應標頭（名稱小寫；同名多值以 ", " 合併）。</param>
public sealed record WebResponse(
    int StatusCode,
    string ReasonPhrase,
    Uri FinalUrl,
    string? MediaType,
    string? Charset,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    bool Truncated);

public sealed record WebDownloadResult(Uri FinalUrl, long Bytes, string? MediaType);

public enum WebFailureKind { InvalidUrl, NameResolution, Connection, Timeout, Tls, ProxyAuthentication, Proxy, TooLarge, TooManyRedirects, Other }

/// <summary>連線層的失敗（HTTP 錯誤碼不算，照實放在 <see cref="WebResponse.StatusCode"/>）。<see cref="Exception.Message"/> 是可以直接給模型與使用者看的繁中句子，不含查詢字串。</summary>
public sealed class WebRequestException(WebFailureKind kind, string message) : Exception(message)
{
    public WebFailureKind Kind { get; } = kind;
}

/// <summary>網路工具用的 HTTP 用戶端（實作在 Lumi.Web 的 <c>WebClient</c>）。所有請求都經過使用者設定的 Proxy。</summary>
public interface IWebClient
{
    /// <summary>
    /// 送出請求並讀取回應。自己處理轉址：最多 <see cref="WebLimits.MaxRedirects"/> 次、只允許 http／https；
    /// 301／302／303 之後改用 GET 且不帶內容，307／308 保留方法與內容。HTTP 錯誤碼不丟例外；
    /// 連線層錯誤丟 <see cref="WebRequestException"/>；<paramref name="ct"/> 取消時丟 <see cref="OperationCanceledException"/>。
    /// </summary>
    Task<WebResponse> SendAsync(WebRequest request, CancellationToken ct);

    /// <summary>
    /// GET 並寫入 <paramref name="destinationPath"/>（呼叫端保證檔案不存在、在範圍內、上層資料夾已存在）。
    /// 先寫到 <c>destinationPath + ".lumi-download"</c>，完成後改名（不覆蓋）；失敗或取消時刪掉暫存檔。
    /// 超過 <paramref name="maxBytes"/> 丟 <see cref="WebFailureKind.TooLarge"/>；狀態碼不是 2xx 丟 <see cref="WebFailureKind.Other"/>「伺服器回應 {碼} {原因}。」。
    /// </summary>
    Task<WebDownloadResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct);
}

public sealed record WebLink(string Text, Uri Url);

/// <param name="Markdown">網頁轉成的 Markdown；連結一律是絕對網址。</param>
/// <param name="Links">整頁所有 http／https 連結（去重、依出現順序）。</param>
/// <param name="UsedReadability">true＝取的是主要內容；false＝整頁。</param>
public sealed record WebPage(string? Title, string Markdown, IReadOnlyList<WebLink> Links, bool UsedReadability);

/// <summary>HTML 轉文字（實作在 Lumi.Web 的 <c>HtmlContentExtractor</c>）。兩個方法都不丟例外。</summary>
public interface IWebContentExtractor
{
    /// <summary><paramref name="readable"/>＝true 先取主要內容，取不到時退回整頁；false＝整頁。</summary>
    WebPage Extract(string html, Uri baseUrl, bool readable);

    /// <summary>位元組轉文字：BOM → <paramref name="charset"/> → （HTML 才看）meta charset → UTF-8。支援 Big5、GBK 等舊編碼。</summary>
    string DecodeText(byte[] body, string? charset, bool isHtml);
}

public sealed record FeedItem(string Title, Uri? Link, DateTimeOffset? Published, string Summary);

public sealed record Feed(string Title, IReadOnlyList<FeedItem> Items);

/// <summary>RSS 2.0／Atom 解析（實作在 Lumi.Web 的 <c>FeedReader</c>）。</summary>
public interface IFeedReader
{
    /// <summary>不是 RSS／Atom 時回 null，不丟例外。<see cref="FeedItem.Summary"/> 已去掉 HTML、合併空白、最多 300 字。</summary>
    Feed? Parse(byte[] body);
}
