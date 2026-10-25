namespace Lumi.Core.Browser;

// 暫時實作（M18 包 18G 為了編譯與測試而放）：包 18A 會取代這個檔案，合併時以 18A 的版本為準。
public static class BrowserRules
{
    /// <summary>null 或空白當作 down；不分大小寫；其他值丟出錯誤說明。</summary>
    public static bool TryParseDirection(string? raw, out BrowserScrollDirection direction, out string error)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
        {
            direction = BrowserScrollDirection.Down;
            error = "";
            return true;
        }
        switch (text.ToLowerInvariant())
        {
            case "up": direction = BrowserScrollDirection.Up; error = ""; return true;
            case "down": direction = BrowserScrollDirection.Down; error = ""; return true;
            case "top": direction = BrowserScrollDirection.Top; error = ""; return true;
            case "bottom": direction = BrowserScrollDirection.Bottom; error = ""; return true;
            default:
                direction = BrowserScrollDirection.Down;
                error = "direction 只能是 up、down、top 或 bottom。";
                return false;
        }
    }

    /// <summary>null＝預設；其餘夾在最小與最大之間。</summary>
    public static int TryParseSnapshotChars(int? value) =>
        Math.Clamp(value ?? BrowserLimits.SnapshotDefaultChars, BrowserLimits.SnapshotMinChars, BrowserLimits.SnapshotMaxChars);
}
