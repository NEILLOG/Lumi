namespace Lumi.Core.Browser;

/// <summary>瀏覽器工具的參數規則（M18，純函式，工具與測試共用）。錯誤文字可以直接回給模型。</summary>
public static class BrowserRules
{
    public const int MaxUrlContainsChars = 200;
    public const int MaxTextVisibleChars = 100;

    /// <summary><c>browser_press</c> 可以按的鍵（序數比較不分大小寫）。</summary>
    public static readonly string[] AllowedKeys =
    [
        "Enter", "Tab", "Escape", "Backspace", "Delete", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
        "Home", "End", "PageUp", "PageDown", "Space",
    ];

    /// <summary><c>browser_download</c> 不存的副檔名（與 <c>download_file</c> 相同）。</summary>
    public static readonly string[] BlockedDownloadExtensions =
    [
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".sh", ".command", ".app", ".pkg", ".dmg",
    ];

    public static bool TryParseKey(string? raw, out string key, out string error)
    {
        key = "";
        var value = raw?.Trim() ?? "";
        if (value.Length == 0)
        {
            error = "缺少 key（要按的鍵）。";
            return false;
        }

        foreach (var allowed in AllowedKeys)
        {
            if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase))
            {
                key = allowed;
                error = "";
                return true;
            }
        }

        error = $"不支援按鍵「{value}」，可用：{string.Join('、', AllowedKeys)}。";
        return false;
    }

    /// <summary>null 或空白當作 <see cref="BrowserScrollDirection.Down"/>。</summary>
    public static bool TryParseDirection(string? raw, out BrowserScrollDirection direction, out string error)
    {
        direction = BrowserScrollDirection.Down;
        error = "";
        var value = raw?.Trim() ?? "";
        if (value.Length == 0)
            return true;

        if (value.Equals("up", StringComparison.OrdinalIgnoreCase)) direction = BrowserScrollDirection.Up;
        else if (value.Equals("down", StringComparison.OrdinalIgnoreCase)) direction = BrowserScrollDirection.Down;
        else if (value.Equals("top", StringComparison.OrdinalIgnoreCase)) direction = BrowserScrollDirection.Top;
        else if (value.Equals("bottom", StringComparison.OrdinalIgnoreCase)) direction = BrowserScrollDirection.Bottom;
        else
        {
            error = "direction 只能是 up、down、top 或 bottom。";
            return false;
        }

        return true;
    }

    /// <returns>錯誤文字；沒有問題時 null。</returns>
    public static string? ValidateWait(
        string? urlContains, string? textVisible, int? timeoutSeconds, out BrowserWaitCondition condition, out TimeSpan timeout)
    {
        var url = string.IsNullOrWhiteSpace(urlContains) ? null : urlContains.Trim();
        var text = string.IsNullOrWhiteSpace(textVisible) ? null : textVisible.Trim();
        condition = new BrowserWaitCondition(url, text);
        timeout = TimeSpan.FromSeconds(Math.Clamp(
            timeoutSeconds ?? BrowserLimits.WaitDefaultSeconds, BrowserLimits.WaitMinSeconds, BrowserLimits.WaitMaxSeconds));

        if (url is null && text is null)
            return "請用 done_when_url_contains 或 done_when_text 指定「完成」的條件，Lumi 才知道什麼時候可以繼續。";
        if (url is { Length: > MaxUrlContainsChars })
            return $"done_when_url_contains 太長（超過 {MaxUrlContainsChars} 字）。";
        if (text is { Length: > MaxTextVisibleChars })
            return $"done_when_text 太長（超過 {MaxTextVisibleChars} 字）。";
        return null;
    }

    /// <summary>快照字數上限：null＝預設，其餘夾在最小與最大之間。</summary>
    public static int TryParseSnapshotChars(int? value) =>
        value is null
            ? BrowserLimits.SnapshotDefaultChars
            : Math.Clamp(value.Value, BrowserLimits.SnapshotMinChars, BrowserLimits.SnapshotMaxChars);
}
