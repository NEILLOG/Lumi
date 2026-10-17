using System.Text.Json;
using global::Anthropic.Exceptions;
using Lumi.Core.Chat;

namespace Lumi.Providers.Anthropic;

internal static class AnthropicErrorMapper
{
    public static ChatFailure ToChatFailure(Exception ex) => ex switch
    {
        AnthropicUnauthorizedException e => Make(ChatFailureKind.InvalidApiKey, "Anthropic 金鑰無效或已失效，請重新輸入。", false, e),
        AnthropicForbiddenException e => Make(ChatFailureKind.PermissionDenied, "這個帳號沒有權限使用此模型。", false, e),
        AnthropicNotFoundException e => Make(ChatFailureKind.ModelNotFound, "找不到指定的模型。", false, e),
        // RetryAfter／RequestId：SDK 這兩個例外目前沒有公開對應的屬性可讀，先留 null；
        // 之後確認正確的成員名稱再補（不影響 ChatFailureKind 的分類是否正確）。
        AnthropicRateLimitException e => Make(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, e),
        AnthropicBadRequestException e => Make(ChatFailureKind.BadRequest, "請求格式不正確。", false, e),
        AnthropicUnprocessableEntityException e => Make(ChatFailureKind.BadRequest, "請求內容不正確。", false, e),
        AnthropicApiException e when (int)e.StatusCode == 529 => Make(ChatFailureKind.Overloaded, "服務暫時過載，請稍後再試。", true, e),
        AnthropicApiException e when (int)e.StatusCode >= 500 => Make(ChatFailureKind.ServerError, "Anthropic 服務發生錯誤，請稍後再試。", true, e),
        AnthropicApiException e => Make(ChatFailureKind.StreamError, "與 Anthropic 的連線發生錯誤。", true, e),
        AnthropicSseException e => MapSseException(e),
        AnthropicIOException or HttpRequestException or System.Net.Sockets.SocketException
            => Make(ChatFailureKind.Network, "無法連線到 Anthropic。", true, ex),
        TimeoutException => Make(ChatFailureKind.Network, "連線逾時。", true, ex),
        _ => Make(ChatFailureKind.Unknown, "發生未預期的錯誤。", false, ex),
    };

    /// <summary>
    /// 串流中途收到 <c>event: error</c> 時，SDK 一律丟 <see cref="AnthropicSseException"/>，
    /// 把 stop_reason、overloaded_error 這些區別都壓平了；但伺服器原始的錯誤 JSON 會原封不動放在
    /// <see cref="Exception.Message"/> 裡（實測得到，不是文件保證的行為），所以從裡面撈 error.type
    /// 來還原 Overloaded 這個比較重要的分類；撈不到就當一般串流錯誤。
    /// </summary>
    private static ChatFailure MapSseException(AnthropicSseException e)
    {
        if (TryExtractServerErrorType(e.Message) == "overloaded_error")
            return Make(ChatFailureKind.Overloaded, "服務暫時過載，請稍後再試。", true, e);

        return Make(ChatFailureKind.StreamError, "串流資料解析失敗。", true, e);
    }

    private static string? TryExtractServerErrorType(string message)
    {
        // 觀察到的實際訊息格式：SSE error returned from server: '{...}'（結尾還有一個單引號，
        // 不能直接從第一個 { 切到字串結尾，否則 JSON 後面多了垃圾字元會解析失敗）。
        var start = message.IndexOf('{');
        var end = message.LastIndexOf('}');
        if (start < 0 || end < start) return null;

        try
        {
            using var doc = JsonDocument.Parse(message[start..(end + 1)]);
            return doc.RootElement.GetProperty("error").GetProperty("type").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatFailure Make(ChatFailureKind kind, string message, bool retryable, Exception ex, TimeSpan? retryAfter = null) =>
        new(kind, message, retryable, retryAfter);
}
