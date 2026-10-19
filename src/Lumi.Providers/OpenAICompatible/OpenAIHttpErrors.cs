using System.Net;
using Lumi.Core.Chat;

namespace Lumi.Providers.OpenAICompatible;

internal static class OpenAIHttpErrors
{
    /// <summary>
    /// 依 HTTP 狀態碼分類。不把伺服器回的錯誤內文放進訊息（可能含敏感資訊）；
    /// 公司 Gateway 或 Ollama 的細節看 log 裡的狀態碼與 request id 即可。
    /// </summary>
    public static ChatFailure FromResponse(HttpResponseMessage response, bool requestHadTools = false)
    {
        var status = (int)response.StatusCode;
        return status switch
        {
            401 => new(ChatFailureKind.InvalidApiKey, "金鑰無效或已失效，請重新輸入。", false),
            403 => new(ChatFailureKind.PermissionDenied, "這個帳號沒有權限使用此模型。", false),
            404 => new(ChatFailureKind.ModelNotFound, "找不到指定的模型，或 Base URL 不正確（通常需要包含 /v1）。", false),
            // 請求帶了工具定義時，400 最常見的原因是這個模型／伺服器不支援工具（例如小型本機模型）
            400 or 422 when requestHadTools => new(ChatFailureKind.BadRequest,
                "請求內容不被伺服器接受。這個模型可能不支援工具，可以在模型設定關掉「使用工具」再試一次。", false),
            400 or 413 or 422 => new(ChatFailureKind.BadRequest, "請求內容不被伺服器接受。", false),
            429 => new(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, RetryAfter(response)),
            529 => new(ChatFailureKind.Overloaded, "服務暫時過載，請稍後再試。", true),
            >= 500 => new(ChatFailureKind.ServerError, "伺服器發生錯誤，請稍後再試。", true),
            _ => new(ChatFailureKind.Unknown, $"伺服器回應了未預期的狀態碼 {status}。", false),
        };
    }

    public static ChatFailure FromException(Exception ex, Uri baseUri) => ex switch
    {
        HttpRequestException or IOException =>
            new(ChatFailureKind.Network, $"無法連線到 {baseUri.Host}，請確認 Base URL 與服務是否已啟動。", true),
        TimeoutException or TaskCanceledException =>
            new(ChatFailureKind.Network, "連線逾時。", true),
        _ => new(ChatFailureKind.Unknown, "發生未預期的錯誤。", false),
    };

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow is { Ticks: > 0 } wait ? wait : null;
        return null;
    }
}
