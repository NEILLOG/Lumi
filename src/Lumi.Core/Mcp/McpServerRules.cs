using System.Globalization;
using System.Text.RegularExpressions;
using Lumi.Core.Speech;

namespace Lumi.Core.Mcp;

/// <summary>
/// MCP 伺服器欄位的驗證規則（純函式，設定頁與 JSON 匯入都用它）。回傳 null 表示通過，否則回傳要顯示給使用者的繁中錯誤文字（M16）。
/// </summary>
public static partial class McpServerRules
{
    private const int MaxNameLength = 40;
    private const int MinTimeoutSeconds = 5;
    private const int MaxTimeoutSeconds = 600;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex EnvName();

    [GeneratedRegex(@"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+\z")]
    private static partial Regex HeaderToken();

    /// <summary>由 Lumi 自動設定的標頭，使用者不可以自己指定。</summary>
    private static readonly string[] ReservedHeaders = ["Host", "Content-Length", "Mcp-Session-Id"];

    public static string? ValidateName(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
            return "請輸入名稱";
        if (text.Length > MaxNameLength)
            return "名稱最多 40 個字";
        return null;
    }

    public static string? ValidateCommand(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "請輸入指令" : null;
    }

    /// <summary>必須是絕對網址；只有 https 可以對外，http 只允許本機；不可含帳號密碼（標頭才是放驗證資訊的地方）。</summary>
    public static string? ValidateUrl(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
            return "請輸入 URL";

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
            return "請輸入完整的網址";

        if (uri.Scheme == Uri.UriSchemeHttp && !SpeechEndpointRules.IsLocalHost(uri.Host))
            return "遠端伺服器必須使用 https";

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "網址不可包含帳號密碼，請改用標頭";

        return null;
    }

    /// <summary>代號：格式同 <see cref="McpToolNaming.IsValidKey"/>，且不能和其他伺服器重複。</summary>
    public static string? ValidateKey(string? key, IEnumerable<string> otherKeys)
    {
        if (key is null || !McpToolNaming.IsValidKey(key))
            return "只能使用小寫英文、數字與 -，最多 16 個字";
        if (otherKeys.Contains(key, StringComparer.Ordinal))
            return "這個代號已經被其他伺服器使用";
        return null;
    }

    public static string? ValidateTimeout(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            || seconds < MinTimeoutSeconds
            || seconds > MaxTimeoutSeconds)
            return "請輸入 5 到 600 之間的整數";
        return null;
    }

    public static string? ValidateEnvName(string? value)
    {
        return value is not null && EnvName().IsMatch(value) ? null : "名稱只能使用英文、數字與底線，不能以數字開頭";
    }

    public static string? ValidateHeaderName(string? value)
    {
        if (value is null || !HeaderToken().IsMatch(value))
            return "標頭名稱格式不正確";
        if (ReservedHeaders.Contains(value, StringComparer.OrdinalIgnoreCase))
            return "這個標頭由 Lumi 自動設定";
        return null;
    }

    /// <summary>依換行切開，去掉每行頭尾空白與空行（多行文字框的參數）。</summary>
    public static IReadOnlyList<string> ParseArgs(string multiline)
    {
        return multiline.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
