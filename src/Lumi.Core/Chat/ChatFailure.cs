namespace Lumi.Core.Chat;

/// <summary>
/// <see cref="Message"/> 是給使用者看的繁中說明，不得包含金鑰內容；
/// 供應商的原始錯誤訊息（可能含敏感資訊）不放在這裡，只在 log 記 request id。
/// </summary>
public sealed record ChatFailure(
    ChatFailureKind Kind,
    string Message,
    bool Retryable,
    TimeSpan? RetryAfter = null,
    string? RequestId = null);

public enum ChatFailureKind
{
    /// <summary>沒有可用的模型設定（全部刪光了，或預設模型指向不存在的連線）。</summary>
    ModelNotConfigured,

    /// <summary><see cref="Lumi.Core.Secrets.ISecretStore"/> 沒有金鑰；1D 跳出輸入對話框。</summary>
    MissingApiKey,

    /// <summary>金鑰存在但讀不出來（<see cref="Lumi.Core.Secrets.SecretStoreException"/>）。</summary>
    SecretUnreadable,

    /// <summary>HTTP 401。</summary>
    InvalidApiKey,

    /// <summary>HTTP 403。</summary>
    PermissionDenied,

    /// <summary>HTTP 404：模型不存在或帳號無法使用。</summary>
    ModelNotFound,

    /// <summary>HTTP 400、413。</summary>
    BadRequest,

    /// <summary>HTTP 429，可能帶 <see cref="ChatFailure.RetryAfter"/>。</summary>
    RateLimited,

    /// <summary>HTTP 529：服務暫時過載。</summary>
    Overloaded,

    /// <summary>其他 5xx。</summary>
    ServerError,

    /// <summary>連不上、DNS、TLS、逾時等連線層級的問題。</summary>
    Network,

    /// <summary>串流中途收到 error 事件，或連線中斷。</summary>
    StreamError,

    /// <summary><c>stop_reason: refusal</c>（含開啟 fallback 後，所有候補模型也都拒絕）。</summary>
    Refused,

    /// <summary>工具迴圈達到次數上限；已完成的步驟都保留，使用者可以「重試」接著跑。</summary>
    ToolLoopLimit,

    /// <summary>程式檔案不完整（載入組件時 <see cref="System.IO.FileNotFoundException"/>／<see cref="System.IO.FileLoadException"/>，M22）。</summary>
    AppFilesMissing,

    /// <summary>請求超過模型能處理的長度（M22 上下文壓縮會先自動整理再重送一次）。</summary>
    ContextTooLong,

    Unknown,
}
