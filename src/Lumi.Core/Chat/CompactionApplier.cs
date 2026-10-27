namespace Lumi.Core.Chat;

/// <summary>
/// 把整理狀態（<see cref="CompactionState"/>）套到歷史上，得到「要送給模型的歷史」。純函式，不改存檔：
/// <list type="number">
/// <item><c>seq</c> ≤ <see cref="CompactionState.SummaryUptoSeq"/> 的訊息拿掉，改在第一則使用者訊息前面加一段 <c>&lt;conversation_summary&gt;</c>。</item>
/// <item><c>seq</c> ≤ <see cref="CompactionState.ToolResultsUptoSeq"/> 的工具結果內容換成一行省略說明，圖片一併拿掉（<c>IsError</c> 與成對結構照舊）。</item>
/// <item>被保留的助理訊息，除了最後一則使用者訊息之後的（進行中的工具迴圈）以外，拿掉 thinking 區塊：整理後前綴變了，留著舊簽章反而可能被 400。</item>
/// </list>
/// 沒有整理時原樣回傳（system 與工具快照不受影響，由呼叫端維持）。結果之後仍要交給
/// <see cref="ConversationService.BuildRequestMessages"/> 整理成成對、交替的訊息。
/// </summary>
public static class CompactionApplier
{
    /// <summary>第一層：最近這麼多則使用者訊息（含目前這則）之後的工具結果不動。</summary>
    public const int ToolResultsKeepUserMessages = 3;

    /// <summary>第二層：最近這麼多則使用者訊息（含目前這則）原樣保留。</summary>
    public const int SummaryKeepUserMessages = 2;

    public static IReadOnlyList<ChatMessage> Apply(IReadOnlyList<ChatMessage> history, CompactionState state)
    {
        if (state.IsEmpty)
            return history;

        var summaryUpto = state.SummaryUptoSeq ?? 0;
        var toolUpto = state.ToolResultsUptoSeq ?? 0;
        var lastUserSeq = history.LastOrDefault(m => m.Role == ChatRole.User)?.Seq ?? 0;

        var result = new List<ChatMessage>(history.Count);
        foreach (var message in history)
        {
            if (message.Seq <= summaryUpto)
                continue;

            var kept = message;
            if (message.Role == ChatRole.Tool && message.Seq <= toolUpto)
                kept = ElideToolResults(message);
            else if (message.Role == ChatRole.Assistant && message.Seq <= lastUserSeq)
                kept = WithoutThinking(message);
            result.Add(kept);
        }

        if (summaryUpto > 0 && !string.IsNullOrWhiteSpace(state.Summary))
        {
            var block = new TextBlock(SummaryPrefix(state.Summary));
            var firstUser = result.FindIndex(m => m.Role == ChatRole.User);
            if (firstUser >= 0)
                result[firstUser] = result[firstUser] with { Content = [block, .. result[firstUser].Content] };
            else
                result.Insert(0, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [block], DateTimeOffset.UtcNow));
        }
        return result;
    }

    /// <summary>
    /// 第一層的邊界：倒數第 3 則使用者訊息之前的訊息（回傳它們的最大 <c>seq</c>）。使用者訊息不足 3 則回 0（沒有東西可整理）。
    /// 最近的工具結果不動。
    /// </summary>
    public static int ToolResultsBoundary(IReadOnlyList<ChatMessage> history) => BoundaryBeforeLastUserMessages(history, ToolResultsKeepUserMessages);

    /// <summary>第二層的邊界：倒數第 2 則使用者訊息之前（最近兩輪原樣保留）。不足 2 則回 0。</summary>
    public static int SummaryBoundary(IReadOnlyList<ChatMessage> history) => BoundaryBeforeLastUserMessages(history, SummaryKeepUserMessages);

    private static int BoundaryBeforeLastUserMessages(IReadOnlyList<ChatMessage> history, int keep)
    {
        var users = history.Where(m => m.Role == ChatRole.User && m.Seq > 0).ToList();
        return users.Count >= keep ? Math.Max(users[^keep].Seq - 1, 0) : 0;
    }

    /// <summary>摘要放進第一則使用者訊息前面的文字。</summary>
    public static string SummaryPrefix(string summary) =>
        $"<conversation_summary>\n這是先前對話的摘要（較早的內容已整理成這份摘要，原文不再提供）：\n\n{summary.Trim()}\n</conversation_summary>";

    /// <summary>被省略的工具結果換成的一行說明。</summary>
    public static string ElidedText(ToolResultBlock result) =>
        string.IsNullOrWhiteSpace(result.Target)
            ? $"（較早的工具結果已省略：{result.DisplayName}）"
            : $"（較早的工具結果已省略：{result.DisplayName} · {result.Target}）";

    private static ChatMessage ElideToolResults(ChatMessage message) =>
        message with
        {
            Content = [.. message.Content.Select(block => block is ToolResultBlock result
                ? result with { Content = ElidedText(result), Images = null }
                : block)],
        };

    private static ChatMessage WithoutThinking(ChatMessage message) =>
        message.Content.Any(b => b is ThinkingBlock or RedactedThinkingBlock)
            ? message with { Content = [.. message.Content.Where(b => b is not (ThinkingBlock or RedactedThinkingBlock))] }
            : message;

    // ---- 摘要用的輸入 ----

    /// <summary>固定的整理指示：接在要整理的內容後面，當作最後一則使用者訊息。</summary>
    public const string SummaryInstruction =
        """
        請把以上的對話整理成一份摘要。這份摘要會取代以上的原文，讓對話可以接著進行，所以要讓人只看摘要也能繼續。依序列出：
        1. 使用者的目標與要求
        2. 做過的決定與結論
        3. 重要的事實與數字（原樣保留，不要改寫或四捨五入）
        4. 用過的檔案、網址與工具結果的重點
        5. 尚未完成的事項
        如果開頭已經有先前的摘要（<conversation_summary>），把它的內容一併整理進來。
        用這段對話所使用的語言書寫。只根據以上的內容，不要加入原文沒有的資訊。不要呼叫工具，只輸出摘要本身。
        """;

    /// <summary>
    /// 摘要呼叫的輸入：<paramref name="history"/> 中 <c>seq</c> ≤ <paramref name="boundary"/> 的部分（已套用整理狀態與第一層），
    /// 工具呼叫與結果、思考區塊改成純文字（摘要呼叫不帶工具，帶著 tool_use 區塊會被 API 拒絕），最後接上 <see cref="SummaryInstruction"/>。
    /// 開頭是使用者訊息、使用者與助理交替。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildSummaryInput(IReadOnlyList<ChatMessage> history, CompactionState state, int boundary)
    {
        // 摘要涵蓋的範圍內，工具結果一律先省略（第一層的邊界依整段歷史算，不一定已經存過檔）
        var toolUpto = Math.Max(state.ToolResultsUptoSeq ?? 0, ToolResultsBoundary(history));
        var effective = state with { ToolResultsUptoSeq = toolUpto > 0 ? toolUpto : state.ToolResultsUptoSeq };

        var scope = history.Where(m => m.Seq <= boundary).ToList();
        var built = ConversationService.BuildRequestMessages(Apply(scope, effective with { AppliedAtSeq = 0 }));

        var flat = new List<ChatMessage>();
        foreach (var message in built)
        {
            var role = message.Role == ChatRole.Assistant ? ChatRole.Assistant : ChatRole.User;
            var blocks = message.Content.SelectMany(Flatten).ToList();
            if (blocks.Count == 0)
                continue;
            if (flat.Count > 0 && flat[^1].Role == role)
                flat[^1] = flat[^1] with { Content = [.. flat[^1].Content, .. blocks] };
            else
                flat.Add(message with { Role = role, Content = blocks, TurnSuffix = null });
        }

        var instruction = new TextBlock(SummaryInstruction);
        if (flat.Count > 0 && flat[^1].Role == ChatRole.User)
            flat[^1] = flat[^1] with { Content = [.. flat[^1].Content, instruction] };
        else
            flat.Add(new ChatMessage(ChatMessage.NewId(), ChatRole.User, [instruction], DateTimeOffset.UtcNow));
        return flat;
    }

    private static IEnumerable<ContentBlock> Flatten(ContentBlock block)
    {
        switch (block)
        {
            case ToolUseBlock use:
                yield return new TextBlock($"[呼叫工具 {use.Name}] {use.InputJson}");
                break;
            case ToolResultBlock result:
                var target = string.IsNullOrWhiteSpace(result.Target) ? "" : $" · {result.Target}";
                yield return new TextBlock($"[工具結果：{result.DisplayName}{target}{(result.IsError ? "（失敗）" : "")}]\n{result.Content}");
                break;
            case ThinkingBlock or RedactedThinkingBlock or ProviderBlock:
                break;
            default:
                yield return block;
                break;
        }
    }
}
