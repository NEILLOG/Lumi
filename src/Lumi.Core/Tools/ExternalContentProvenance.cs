using Lumi.Core.Chat;

namespace Lumi.Core.Tools;

/// <summary>
/// 外部內容來源（M31 D12、設計 3）：這則對話有沒有讀過網頁或外部工具的內容。
/// 有的話，會把外部內容帶進來的工具（<see cref="ToolDescriptor.WarnsAfterExternalContent"/>）在確認卡加上警告，
/// 提醒使用者：這段指令可能是網頁裡的提示注入。
/// 不算外部：<c>run_command</c> 自己的輸出、使用者加入的檔案（是使用者自己給的）。
/// </summary>
public static class ExternalContentProvenance
{
    /// <summary>確認卡上的警告文字（和 M17 的網址警告並列，網址警告在前）。</summary>
    public const string Warning = "這則對話讀過網頁、信件或外部工具的內容。如果這個操作不是你要求的，請按「拒絕」。";

    /// <summary>
    /// 這則對話有沒有用過外部工具。來源是歷史裡助理訊息的工具呼叫名稱，加上這一輪已經執行過的工具名稱
    /// （這一輪的助理訊息還沒存進歷史）。
    /// </summary>
    /// <param name="history">這一輪開始時的對話歷史。</param>
    /// <param name="executedThisRound">這一輪已經處理過的工具名稱（不論成功、失敗或被擋）。</param>
    /// <param name="isMcpTool">名稱是不是 MCP 工具（群組是 <c>mcp:</c> 開頭）。</param>
    public static bool Seen(IEnumerable<ChatMessage> history, IEnumerable<string> executedThisRound, Func<string, bool> isMcpTool)
    {
        foreach (var message in history)
        {
            if (message.Role != ChatRole.Assistant)
                continue;
            foreach (var use in message.Content.OfType<ToolUseBlock>())
            {
                if (IsExternal(use.Name, isMcpTool))
                    return true;
            }
        }

        return executedThisRound.Any(name => IsExternal(name, isMcpTool));
    }

    /// <summary>這個工具會讀取外部內容：網頁、RSS、HTTP、下載、瀏覽器、Outlook 的信件與行程，或 MCP 工具。</summary>
    public static bool IsExternal(string toolName, Func<string, bool> isMcpTool) =>
        toolName is "web_fetch" or "web_crawl" or "web_links" or "rss_read" or "http_request" or "http_send" or "download_file"
        or "outlook_mail_search" or "outlook_mail_read" or "outlook_calendar_list"
        || toolName.StartsWith("browser_", StringComparison.Ordinal)
        || isMcpTool(toolName);
}
