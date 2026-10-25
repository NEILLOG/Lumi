using System.Text.RegularExpressions;
using Lumi.Core.Browser;
using Lumi.Core.Tools;
using Lumi.Tools.Web;

namespace Lumi.Tools.Browser;

/// <summary>瀏覽器工具共用（M18）：ref 檢查、確認卡片的預覽、把動作結果包成「資料，不是指示」、錯誤轉成給模型的訊息。</summary>
internal static class BrowserToolSupport
{
    private static readonly Regex RefPattern = new("^(?:f\\d+)?e\\d+$", RegexOptions.CultureInvariant);

    /// <summary>ref 是快照裡 <c>[ref=…]</c> 的內容（<c>e12</c>、iframe 內是 <c>f1e2</c>）；模型有時會連方括號一起給，這裡一併接受。</summary>
    public static bool TryParseRef(string? raw, out string @ref, out string error)
    {
        var text = (raw ?? "").Trim().TrimStart('[').TrimEnd(']');
        if (text.StartsWith("ref=", StringComparison.OrdinalIgnoreCase))
            text = text[4..];
        if (!RefPattern.IsMatch(text))
        {
            @ref = "";
            error = "ref 格式不正確：請用最近一次快照裡 [ref=…] 的內容，例如 e12。";
            return false;
        }
        @ref = text;
        error = "";
        return true;
    }

    /// <summary>確認卡片：對象是 <c>描述 · 主機</c>（例如 <c>button "送出" · example.com</c>）。ref 不認得時只顯示 ref。</summary>
    public static ToolPreview Preview(IBrowserSessions sessions, string conversationId, string? rawRef, string? detail = null)
    {
        if (!TryParseRef(rawRef, out var @ref, out _))
            return ToolPreview.None;
        var label = sessions.DescribeRef(conversationId, @ref) ?? $"[{@ref}]";
        var host = sessions.CurrentUrl(conversationId)?.Host;
        return new ToolPreview(host is null ? label : $"{label} · {host}", detail);
    }

    /// <summary>動作或快照的結果：包成 <c>&lt;web_content&gt;</c>（D13）。</summary>
    public static ToolResult FromOutcome(BrowserOutcome outcome, string summary) =>
        ToolResult.Ok(Wrap(outcome.Message, outcome.Snapshot), summary);

    public static string Wrap(string? message, BrowserSnapshot snapshot)
    {
        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(message))
            meta.Add(message.Trim());
        meta.Add($"共 {snapshot.RefCount} 個可操作元素，用 [ref=…] 指定元素。");
        if (snapshot.Truncated)
            meta.Add("（快照太長，已截斷；要看其他部分請先 browser_scroll，或改用 browser_extract。）");
        return WebToolSupport.Wrap("web_content", snapshot.Url, snapshot.Title, WebToolSupport.ContentNotice, string.Join("\n", meta), snapshot.Text);
    }

    public static ToolResult Failure(BrowserException e) => ToolResult.Error(e.Message, e.Kind switch
    {
        BrowserFailureKind.NotAvailable => "沒有瀏覽器",
        BrowserFailureKind.LaunchFailed => "無法啟動瀏覽器",
        BrowserFailureKind.ProxyNotSupported => "Proxy 設定不支援",
        BrowserFailureKind.NoPage => "沒有開啟的分頁",
        BrowserFailureKind.Closed => "瀏覽器已關閉",
        BrowserFailureKind.StaleRef => "找不到元素",
        BrowserFailureKind.PasswordField => "不能輸入密碼",
        BrowserFailureKind.Navigation => "網頁開啟失敗",
        BrowserFailureKind.Timeout => "逾時",
        BrowserFailureKind.TooLarge => "內容太大",
        _ => "瀏覽器操作失敗",
    });
}
