using System.Net;
using System.Security.Authentication;
using Lumi.Core.Speech;

namespace Lumi.Speech;

/// <summary>
/// HTTP 狀態碼與例外 → <see cref="SpeechFailure"/>（對應表見 outter_sys_spec/whisper.md）。
/// 訊息一律不含伺服器回應內文與 Token；細節只在狀態碼上。
/// </summary>
internal static class WhisperErrorMapper
{
    public static SpeechFailure FromResponse(HttpResponseMessage response) => FromStatus((int)response.StatusCode);

    public static SpeechFailure FromStatus(int status) => status switch
    {
        401 or 403 => new(SpeechFailureKind.InvalidToken, "Token 無效，或沒有 WHISPER_API 權限。", false),
        404 => new(SpeechFailureKind.NotFound, "找不到路徑，請確認端點。", false),
        413 => new(SpeechFailureKind.TooLarge, "錄音檔太大。", false),
        429 => new(SpeechFailureKind.RateLimited, "請求太頻繁，請稍後再試。", true),
        408 or 504 => new(SpeechFailureKind.ServerTimeout, "處理逾時，請稍後再試。", true),
        >= 500 => new(SpeechFailureKind.ServerError, "語音服務發生錯誤，請稍後再試。", true),
        _ => new(SpeechFailureKind.Unknown, $"語音服務回應了未預期的狀態碼 {status}。", false),
    };

    /// <summary>
    /// 連線層的例外。憑證不受信任（內網 CA 沒有安裝）時 <see cref="HttpRequestException"/> 內會有 <see cref="AuthenticationException"/>；
    /// 這時指引使用者安裝內部 CA，**不提供略過驗證**（Token 會經過這條連線）。
    /// </summary>
    public static SpeechFailure FromException(Exception ex) =>
        HasAuthenticationException(ex)
            ? new(SpeechFailureKind.CertificateUntrusted,
                "憑證不受信任。請把公司的內部憑證授權單位安裝到系統：Windows 請放進「受信任的根憑證授權單位」；macOS 請在「鑰匙圈存取」匯入，並設為「永遠信任」。",
                false)
            : new(SpeechFailureKind.Network, "無法連線，請確認網址、網路或 VPN。", true);

    private static bool HasAuthenticationException(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException)
                return true;
        }
        return false;
    }
}
