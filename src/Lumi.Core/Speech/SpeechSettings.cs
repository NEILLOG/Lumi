namespace Lumi.Core.Speech;

/// <summary>
/// 語音服務的連線設定（M6）。Token 不在這裡，只在 <see cref="Secrets.ISecretStore"/>（名稱見 <see cref="SpeechSecrets"/>）；
/// <paramref name="TokenHint"/> 是末 4 碼，<paramref name="TokenHost"/> 是 Token 綁定的端點主機（換主機就不送 Token）。
/// </summary>
/// <param name="Endpoint">完整的 https 網址；留空表示尚未設定。</param>
/// <param name="Language">語言代碼，留空則不送 <c>language</c>，讓服務端判斷。</param>
public sealed record SpeechSettings(
    string Endpoint = "",
    string Language = SpeechSettings.DefaultLanguage,
    int TimeoutSeconds = SpeechSettings.DefaultTimeoutSeconds,
    string? TokenHint = null,
    string? TokenHost = null)
{
    public const string DefaultLanguage = "zh";
    public const int DefaultTimeoutSeconds = 120;
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 600;

    public static readonly SpeechSettings Default = new("", DefaultLanguage, DefaultTimeoutSeconds, null, null);

    /// <summary>去掉前後空白，逾時 clamp 到 5～600 秒；語言 null（欄位缺漏）時用預設，空白保留為「不送」。</summary>
    public SpeechSettings Normalize() => new(
        Endpoint?.Trim() ?? "",
        Language is null ? DefaultLanguage : Language.Trim(),
        Math.Clamp(TimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds),
        string.IsNullOrWhiteSpace(TokenHint) ? null : TokenHint.Trim(),
        string.IsNullOrWhiteSpace(TokenHost) ? null : TokenHost.Trim());
}
