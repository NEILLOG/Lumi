namespace Lumi.Core.Speech;

/// <summary>端點驗證的結果：有效時帶解析好的 <see cref="Uri"/>，無效時帶繁中原因。</summary>
public sealed record SpeechEndpointCheck(bool IsValid, string? Reason, Uri? Uri)
{
    internal static SpeechEndpointCheck Ok(Uri uri) => new(true, null, uri);

    internal static SpeechEndpointCheck Invalid(string reason) => new(false, reason, null);
}

/// <summary>
/// 語音服務端點的規則（純函式，設定頁與服務都用它）：必須是完整的絕對網址；只有 https 可以對外，
/// http 只允許 localhost、127.0.0.1、::1；不可含帳號密碼、查詢字串或錨點，避免 Token 或參數夾帶到不該去的地方。
/// </summary>
public static class SpeechEndpointRules
{
    public static SpeechEndpointCheck Validate(string? endpoint)
    {
        var text = endpoint?.Trim() ?? "";
        if (text.Length == 0)
            return SpeechEndpointCheck.Invalid("尚未輸入服務端點。");

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return SpeechEndpointCheck.Invalid("端點必須是完整的網址，包含 https:// 與主機名稱。");

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            if (!IsLocalHost(uri.Host))
                return SpeechEndpointCheck.Invalid("只有 localhost、127.0.0.1 或 ::1 可以使用 http，其他主機必須使用 https。");
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return SpeechEndpointCheck.Invalid("端點必須使用 https。");
        }

        if (string.IsNullOrEmpty(uri.Host))
            return SpeechEndpointCheck.Invalid("端點缺少主機名稱。");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return SpeechEndpointCheck.Invalid("端點不可包含帳號或密碼。");
        if (text.Contains('?'))
            return SpeechEndpointCheck.Invalid("端點不可包含查詢字串（? 之後的部分）。");
        if (text.Contains('#'))
            return SpeechEndpointCheck.Invalid("端點不可包含錨點（# 之後的部分）。");

        return SpeechEndpointCheck.Ok(uri);
    }

    /// <summary><see cref="Uri.Host"/> 對 IPv6 會帶中括號，兩種寫法都認。</summary>
    public static bool IsLocalHost(string host) =>
        host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]";
}
