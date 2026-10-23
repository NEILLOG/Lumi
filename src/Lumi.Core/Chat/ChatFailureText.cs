using System.Net;

namespace Lumi.Core.Chat;

/// <summary>
/// 失敗文字的補充（M9 9C）：離線判斷與「約 N 秒後再試」。只在 Core 組字，錯誤列與小精靈氣泡都用
/// <see cref="ChatFailure.Message"/>，所以在 <see cref="ConversationService"/> 一處套用，兩邊不各寫一份。
/// 文字不含金鑰、不含路徑；供應商原始錯誤內容不會進來。
/// </summary>
public static class ChatFailureText
{
    private const string NoNetworkSentence = "看起來沒有網路連線。";
    private const string RetryPhrase = "請稍後再試";

    /// <summary>
    /// 只改 <see cref="ChatFailure.Message"/>，其他欄位原樣保留。
    /// <list type="bullet">
    /// <item>連線失敗、系統沒有任何可用網路介面、目標不是本機或內網：補一句「看起來沒有網路連線。」</item>
    /// <item>429／529 帶 <see cref="ChatFailure.RetryAfter"/>：把「請稍後再試」換成「約 N 秒後再試」（超過 60 秒為「約 N 分鐘」）。</item>
    /// </list>
    /// </summary>
    public static ChatFailure Decorate(ChatFailure failure, bool hasNetworkInterface, bool targetIsLocal)
    {
        var message = failure.Message;
        if (failure.Kind == ChatFailureKind.Network && !hasNetworkInterface && !targetIsLocal)
            message = AppendSentence(message, NoNetworkSentence);
        else if (failure.Kind is ChatFailureKind.RateLimited or ChatFailureKind.Overloaded
                 && failure.RetryAfter is { } wait && wait > TimeSpan.Zero)
            message = WithWait(message, FormatWait(wait));

        return message == failure.Message ? failure : failure with { Message = message };
    }

    /// <summary>
    /// 等待時間的說明：無條件進位到秒；超過 60 秒改用分鐘（同樣無條件進位）。
    /// 例：0.2 秒 → 「約 1 秒」，60 秒 → 「約 60 秒」，61 秒 → 「約 2 分鐘」。
    /// </summary>
    public static string FormatWait(TimeSpan wait)
    {
        var seconds = (long)Math.Ceiling(wait.TotalSeconds);
        if (seconds <= 60)
            return $"約 {seconds} 秒";
        var minutes = (long)Math.Ceiling(seconds / 60.0);
        return $"約 {minutes} 分鐘";
    }

    /// <summary>
    /// 連線目標是否為本機或內網（localhost、loopback、私有位址、link-local、單一標籤主機名稱、.local／.lan 等）。
    /// 這種目標失敗時的說明是「確認服務是否已啟動」，不補離線的句子。判斷不確定時回 false（當作外部主機）。
    /// </summary>
    public static bool IsLocalOrPrivateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        var name = host.Trim().TrimStart('[').TrimEnd(']').TrimEnd('.').ToLowerInvariant();
        if (name is "localhost" || name.EndsWith(".localhost", StringComparison.Ordinal))
            return true;

        if (IPAddress.TryParse(name, out var address))
            return IsPrivateAddress(address);

        // 沒有點的主機名稱（例如公司內網的短名稱）與常見的內網網域後綴
        if (!name.Contains('.', StringComparison.Ordinal))
            return true;
        return name.EndsWith(".local", StringComparison.Ordinal) || name.EndsWith(".lan", StringComparison.Ordinal)
            || name.EndsWith(".internal", StringComparison.Ordinal) || name.EndsWith(".home.arpa", StringComparison.Ordinal);
    }

    /// <summary>從 Base URL 取主機判斷；不是絕對網址時當作外部主機。</summary>
    public static bool IsLocalTarget(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && IsLocalOrPrivateHost(uri.Host);

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            var linkLocal = b[0] == 0xFE && (b[1] & 0xC0) == 0x80;   // fe80::/10
            var uniqueLocal = (b[0] & 0xFE) == 0xFC;                 // fc00::/7
            return linkLocal || uniqueLocal;
        }

        return false;
    }

    private static string AppendSentence(string message, string sentence) =>
        message.EndsWith('。') ? message + sentence : message + " " + sentence;

    /// <summary>把「請稍後再試」換成「約 N 秒後再試」；訊息裡找不到這個詞時附在句尾。</summary>
    private static string WithWait(string message, string wait)
    {
        var replacement = $"{wait}後再試";
        if (message.Contains(RetryPhrase, StringComparison.Ordinal))
            return message.Replace(RetryPhrase, replacement, StringComparison.Ordinal);
        return AppendSentence(message, $"{replacement}。");
    }
}
