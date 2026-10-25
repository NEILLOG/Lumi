namespace Lumi.Core.Browser;

// 暫時實作（包 18H 的工作目錄）：依包 18A 步驟 2 的簽章撰寫，讓 18H 能編譯與測試。
// 協調者合併時以包 18A 的版本為準，這個檔案會被取代。
public static class BrowserRules
{
    /// <summary>瀏覽器工具允許的按鍵（不分大小寫）。</summary>
    public static readonly string[] AllowedKeys =
    [
        "Enter", "Tab", "Escape", "Backspace", "Delete", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
        "Home", "End", "PageUp", "PageDown", "Space",
    ];

    /// <summary>不下載的副檔名（與 download_file 相同，不分大小寫）。</summary>
    public static readonly IReadOnlySet<string> BlockedDownloadExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".sh", ".command", ".app", ".pkg", ".dmg",
    };

    /// <summary>按鍵標準化：去頭尾空白，不分大小寫對照名單，回傳標準寫法。</summary>
    public static bool TryParseKey(string? raw, out string key, out string error)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            key = "";
            error = "缺少 key（要按的鍵）。";
            return false;
        }
        foreach (var allowed in AllowedKeys)
        {
            if (string.Equals(allowed, text, StringComparison.OrdinalIgnoreCase))
            {
                key = allowed;
                error = "";
                return true;
            }
        }
        key = "";
        error = $"不支援按鍵「{text}」，可用：{string.Join("、", AllowedKeys)}。";
        return false;
    }

    /// <summary>等待條件與逾時的驗證；通過時回 null，否則回給模型看的錯誤文字。</summary>
    public static string? ValidateWait(string? urlContains, string? textVisible, int? timeoutSeconds, out BrowserWaitCondition condition, out TimeSpan timeout)
    {
        var url = string.IsNullOrWhiteSpace(urlContains) ? null : urlContains.Trim();
        var text = string.IsNullOrWhiteSpace(textVisible) ? null : textVisible.Trim();
        condition = new BrowserWaitCondition(url, text);
        timeout = TimeSpan.FromSeconds(BrowserLimits.WaitDefaultSeconds);

        if (url is null && text is null)
            return "請用 done_when_url_contains 或 done_when_text 指定「完成」的條件，Lumi 才知道什麼時候可以繼續。";
        if (url is not null && url.Length > 200)
            return "done_when_url_contains 太長（超過 200 字）。";
        if (text is not null && text.Length > 100)
            return "done_when_text 太長（超過 100 字）。";

        var seconds = Math.Clamp(timeoutSeconds ?? BrowserLimits.WaitDefaultSeconds, BrowserLimits.WaitMinSeconds, BrowserLimits.WaitMaxSeconds);
        timeout = TimeSpan.FromSeconds(seconds);
        return null;
    }
}
