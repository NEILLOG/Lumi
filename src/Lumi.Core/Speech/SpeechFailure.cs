namespace Lumi.Core.Speech;

/// <summary>語音轉寫或測試連線的失敗分類，對應 Whisper 服務的錯誤對應表（outter_sys_spec/whisper.md，M6）。</summary>
public enum SpeechFailureKind
{
    /// <summary>端點或 Token 尚未設定，或 Token 綁定的主機與端點不同。</summary>
    NotConfigured,
    InvalidToken,
    NotFound,
    TooLarge,
    RateLimited,
    ServerTimeout,
    ServerError,
    /// <summary>本機逾時（<see cref="SpeechSettings.TimeoutSeconds"/>）。</summary>
    LocalTimeout,
    CertificateUntrusted,
    Network,
    UnrecognizedResponse,
    Unknown,
}

/// <summary>
/// 語音功能的可預期錯誤。<see cref="Exception.Message"/> 是可以直接顯示給使用者的繁體中文；
/// 不含伺服器回應內文，也不含 Token。
/// </summary>
public sealed class SpeechFailure(SpeechFailureKind kind, string message, bool retryable) : Exception(message)
{
    public SpeechFailureKind Kind { get; } = kind;

    /// <summary>值得再試一次（重試轉寫）的錯誤；Token、端點、憑證這類錯誤重試沒有用。</summary>
    public bool Retryable { get; } = retryable;
}
