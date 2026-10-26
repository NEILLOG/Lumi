using System.Globalization;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.App.Services;

/// <summary>
/// 把一則對話轉成 Markdown（純函式，不碰檔案，可以測試）。結構：
/// <c># 標題</c>、匯出時間；使用者訊息為 <c>## 你</c>，助理與工具結果為 <c>## Lumi</c>（連續的同一種角色合併在同一段）。
/// 文字區塊原樣輸出；工具結果一行引用；思考、供應商專屬的區塊略過；圖片與附檔只寫名稱。
/// </summary>
public static class ConversationMarkdownExporter
{
    public static string Export(string title, IReadOnlyList<ChatMessage> messages, DateTimeOffset exportedAt)
    {
        var sections = new List<(string Heading, List<string> Parts)>();
        foreach (var message in messages)
        {
            var parts = PartsOf(message);
            if (parts.Count == 0)
                continue;   // 例如只有工具呼叫（助理訊息裡的 tool_use）：沒有可以匯出的內容，不留空段落

            var heading = message.Role == ChatRole.User ? Strings.Export_HeadingUser : Strings.Export_HeadingAssistant;
            if (sections.Count == 0 || sections[^1].Heading != heading)
                sections.Add((heading, []));
            sections[^1].Parts.AddRange(parts);
        }

        var lines = new List<string>
        {
            $"# {title.ReplaceLineEndings(" ")}",
            "",
            string.Format(CultureInfo.CurrentCulture, Strings.Export_ExportedAtFormat,
                exportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        };
        foreach (var (heading, parts) in sections)
        {
            lines.Add("");
            lines.Add($"## {heading}");
            lines.Add("");
            lines.Add(string.Join("\n\n", parts));
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>預設檔名「標題.md」：去掉檔名不允許的字元；空了就用「新對話」。</summary>
    public static string SuggestedFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(title.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length == 0)
            cleaned = Strings.Workbench_NewConversationTitle;
        return cleaned + ".md";
    }

    private static List<string> PartsOf(ChatMessage message)
    {
        var parts = new List<string>();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case TextBlock text when text.Text.Length > 0:
                    parts.Add(text.Text);
                    break;
                case ToolResultBlock result:
                    parts.Add(ToolLine(result));
                    foreach (var image in result.Images ?? [])
                        parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Export_ImageFormat, image.DisplayName));
                    break;
                case ImageBlock image:
                    parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Export_ImageFormat, image.DisplayName));
                    break;
                case ContextBlock context:
                    parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Export_FileFormat, context.DisplayName));
                    break;
                // ToolUseBlock：結果行已經有工具名稱；ThinkingBlock、RedactedThinkingBlock、ProviderBlock：略過
            }
        }
        return parts;
    }

    /// <summary>「&gt; 工具：{名稱} {對象}（成功／失敗）」。</summary>
    private static string ToolLine(ToolResultBlock result)
    {
        var label = string.IsNullOrEmpty(result.Target) ? result.DisplayName : $"{result.DisplayName} {result.Target}";
        var status = result.IsError ? Strings.Export_ToolFailed : Strings.Export_ToolOk;
        return $"> {string.Format(CultureInfo.CurrentCulture, Strings.Export_ToolFormat, label)}（{status}）";
    }
}
