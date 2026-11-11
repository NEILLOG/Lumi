namespace Lumi.Outlook;

/// <summary>把純文字插進 HTML 信件的最前面（回覆、轉寄時保留原信的格式）。</summary>
internal static class HtmlBodyInserter
{
    /// <summary>
    /// plainText 做 HTML 跳脫、換行改成 <c>&lt;br&gt;</c>，插在第一個 <c>&lt;body…&gt;</c> 標籤之後（不分大小寫）；找不到 body 標籤時插在最前面。
    /// </summary>
    public static string Insert(string html, string plainText) => throw new NotImplementedException(); // TODO(30A)
}
