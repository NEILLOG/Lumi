using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.Core.ContextNotes;

/// <param name="Token">對話文字裡的「圖N」。</param>
/// <param name="AttachmentFileName">附件資料夾裡的檔名；和 <paramref name="FilePath"/> 二選一。</param>
public sealed record TurnImage(string Token, string? AttachmentFileName, string? FilePath, string MediaType, string DisplayName);

/// <param name="LastUserExcerpt">這批最後一則使用者訊息的文字去掉換行後的前 30 字。</param>
public sealed record TurnText(string Text, IReadOnlyList<TurnImage> Images, string? LastUserExcerpt);

/// <summary>把要整理的訊息轉成給書記看的對話文字（計畫 4.6）。</summary>
public static class ContextNoteTurnFormatter
{
    private const string UserHeader = "【使用者】", LumiHeader = "【Lumi】", ToolHeader = "【工具】";
    private const string Omitted = "……（中間省略）……";
    private const string TrimmedNotice = "（較早的對話已省略）";

    /// <summary>要整理的訊息範圍（計畫 4.6）。<paramref name="messages"/> 依 Seq 由小到大。</summary>
    public static IReadOnlyList<ChatMessage> SelectPending(IReadOnlyList<ChatMessage> messages, int uptoSeq, string? uptoMessageId)
    {
        ArgumentNullException.ThrowIfNull(messages);
        int start;
        if (uptoSeq > 0 && IsRegenerated(messages, uptoSeq, uptoMessageId))
        {
            // 尾端被重新產生過：從「Seq ≤ uptoSeq 的最後一則有文字的使用者訊息」開始重新整理
            start = -1;
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i].Seq <= uptoSeq && messages[i].Role == ChatRole.User && HasText(messages[i]))
                {
                    start = i;
                    break;
                }
            }
            start = Math.Max(start, 0);
        }
        else
        {
            start = 0;
            while (start < messages.Count && messages[start].Seq <= uptoSeq)
                start++;
            if (start == messages.Count)
                return [];
        }

        // 第一則不是使用者訊息時，往前補到最近一則使用者訊息（含）
        if (start < messages.Count && messages[start].Role != ChatRole.User)
        {
            for (var i = start - 1; i >= 0; i--)
            {
                if (messages[i].Role == ChatRole.User)
                {
                    start = i;
                    break;
                }
            }
        }
        return messages.Skip(start).ToList();
    }

    /// <summary>把訊息轉成對話文字；超過 <paramref name="maxTotalChars"/> 字時只留最後的部分（從行的開頭切）。</summary>
    public static TurnText Format(IReadOnlyList<ChatMessage> messages, int? maxTotalChars = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var batch = messages.Count > ContextNoteLimits.TurnMaxMessages
            ? messages.Skip(messages.Count - ContextNoteLimits.TurnMaxMessages).ToList()
            : messages.ToList();

        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
            foreach (var use in message.Content.OfType<ToolUseBlock>())
                toolNames.TryAdd(use.Id, use.Name);

        var images = new List<TurnImage>();
        var sections = new List<string>();
        foreach (var message in batch)
        {
            var lines = new List<string>();
            foreach (var block in message.Content)
                AddBlock(block, lines, images, toolNames);
            if (lines.Count == 0)
                continue;
            var header = message.Role switch
            {
                ChatRole.Assistant => LumiHeader,
                ChatRole.Tool => ToolHeader,
                _ => UserHeader,
            };
            sections.Add(string.Join("\n", [header, .. lines]));
        }

        var text = string.Join("\n\n", sections);
        if (maxTotalChars is { } max && text.Length > max)
        {
            var cut = text.Length - max;
            var lineStart = text.IndexOf('\n', cut);
            text = lineStart < 0 ? TrimmedNotice : $"{TrimmedNotice}\n{text[(lineStart + 1)..]}";
            // 切掉的部分裡的圖片不再留著（書記看不到對應的圖N，留了也沒有用）
            images = images.Where(i => text.Contains($"[圖片 {i.Token}：", StringComparison.Ordinal)).ToList();
        }

        var lastUser = batch.LastOrDefault(m => m.Role == ChatRole.User && HasText(m));
        var excerpt = lastUser is null ? null : Excerpt(TextOf(lastUser)!);
        return new TurnText(text, images, excerpt);
    }

    private static bool IsRegenerated(IReadOnlyList<ChatMessage> messages, int uptoSeq, string? uptoMessageId)
    {
        var atSeq = messages.FirstOrDefault(m => m.Seq == uptoSeq);
        var lastSeq = messages.Count == 0 ? 0 : messages.Max(m => m.Seq);
        return atSeq is null || atSeq.Id != uptoMessageId || uptoSeq > lastSeq;
    }

    private static void AddBlock(ContentBlock block, List<string> lines, List<TurnImage> images, IReadOnlyDictionary<string, string> toolNames)
    {
        switch (block)
        {
            case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                lines.Add(Truncate(text.Text));
                break;
            case ToolUseBlock use:
                lines.Add($"[呼叫工具：{use.Name}]");
                break;
            case ToolResultBlock result:
                AddToolResult(result, lines, images, toolNames);
                break;
            case ContextBlock context when context.Kind is ContextKind.File or ContextKind.Folder or ContextKind.Image:
                lines.Add($"[附加檔案：{context.DisplayName}]");
                break;
            // 帶回的支線（M36）：標題加摘要全文；支線出處（BranchOrigin）不寫
            case ContextBlock { Kind: ContextKind.BranchSummary } summary:
                var header = $"[帶回的支線：{summary.DisplayName}]";
                lines.Add(string.IsNullOrEmpty(summary.Content) ? header : $"{header}\n{summary.Content}");
                break;
            case ImageBlock image:
                AddImage(image.FileName, image.MediaType, image.DisplayName, lines, images);
                break;
            // 思考區塊、供應商專屬區塊、排程與最近對話的脈絡：略過
        }
    }

    private static void AddToolResult(ToolResultBlock result, List<string> lines, List<TurnImage> images, IReadOnlyDictionary<string, string> toolNames)
    {
        var name = !string.IsNullOrWhiteSpace(result.DisplayName)
            ? result.DisplayName
            : toolNames.TryGetValue(result.ToolUseId, out var toolName) ? toolName : "工具";
        var target = string.IsNullOrWhiteSpace(result.Target) ? "" : $" · {result.Target}";
        var failed = result.IsError ? "（失敗）" : "";
        var header = $"[工具結果：{name}{target}{failed}]";
        lines.Add(string.IsNullOrWhiteSpace(result.Content)
            ? header
            : $"{header}\n{Head(result.Content, ContextNoteLimits.TurnToolResultMaxChars)}");

        foreach (var image in result.Images ?? [])
            AddImage(image.FileName, image.MediaType, image.DisplayName, lines, images);
        foreach (var path in result.ImageFiles ?? [])
        {
            var fileName = Path.GetFileName(path);
            var token = NextToken(images);
            lines.Add($"[圖片 {token}：{fileName}]");
            images.Add(new TurnImage(token, null, path, MediaTypeOf(path), fileName));
        }
    }

    private static void AddImage(string attachmentFileName, string mediaType, string displayName, List<string> lines, List<TurnImage> images)
    {
        var token = NextToken(images);
        lines.Add($"[圖片 {token}：{displayName}]");
        images.Add(new TurnImage(token, attachmentFileName, null, mediaType, displayName));
    }

    /// <summary>圖N：N 在這一次 Format 裡從 1 起。</summary>
    private static string NextToken(List<TurnImage> images) => $"圖{images.Count + 1}";

    private static string MediaTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private static bool HasText(ChatMessage message) =>
        message.Content.OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text));

    private static string? TextOf(ChatMessage message)
    {
        var text = string.Join("\n", message.Content.OfType<TextBlock>().Select(t => t.Text));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string Excerpt(string text)
    {
        var flat = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length > ContextNoteLimits.TriggerExcerptLength ? flat[..ContextNoteLimits.TriggerExcerptLength] : flat;
    }

    private static string Truncate(string text)
    {
        if (text.Length <= ContextNoteLimits.TurnTextMaxChars)
            return text;
        return $"{text[..ContextNoteLimits.TurnTextHead]}\n{Omitted}\n{text[^ContextNoteLimits.TurnTextTail..]}";
    }

    private static string Head(string text, int max) => text.Length > max ? text[..max] : text;
}
