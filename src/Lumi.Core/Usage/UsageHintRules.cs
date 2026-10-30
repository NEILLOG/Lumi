using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lumi.Core.Chat;

namespace Lumi.Core.Usage;

/// <summary>
/// 判斷一次回應結束後的輸入（<see cref="UsageHintRules.AfterResponse"/>）。
/// <paramref name="History"/> 是整則對話的訊息，依 <see cref="ChatMessage.Seq"/> 由小到大；
/// <paramref name="ContextUsed"/> 與 <paramref name="ContextLimit"/> 是目前上下文的估算與上限（圓環用的同一組數字）；
/// <paramref name="TurnCount"/> 是使用者訊息數；<paramref name="LastToolDefinitionTokens"/> 是這次請求的工具定義估算；
/// <paramref name="IsFirstResponseInConversation"/> 是這次回應是否為這則對話的第一次回應。
/// </summary>
public sealed record HintInput(
    IReadOnlyList<ChatMessage> History,
    CompactionState Compaction,
    int ContextUsed,
    int ContextLimit,
    int TurnCount,
    int LastToolDefinitionTokens,
    bool IsFirstResponseInConversation);

/// <summary>
/// 使用習慣提示的判斷（plan/23-usage-cost.md 第 7.2 節）。純邏輯，不讀資料庫、不看時鐘（時間由呼叫端傳入）。
/// 門檻一律來自 <see cref="UsageHintThresholds"/>。
/// 第 1、7 條需要使用者動作，不在這裡判斷（由畫面層處理）。
/// 第 3 條由 <see cref="OnResume"/> 判斷；其餘 6 條由 <see cref="AfterResponse"/> 判斷。
/// 提示文字的格式參數（<see cref="UsageHintMatch.Args"/>）：
/// <list type="bullet">
/// <item>第 2 條 <c>long-conversation</c>：<c>[]</c>。</item>
/// <item>第 3 條 <c>stale-resume</c>：<c>["h:3", "92K"]</c> 或 <c>["d:2", "92K"]</c>。經過時間代碼 <c>h:N</c> 為小時（不足 1 小時以上取整數，至少 1，且 &lt; 24 小時）、<c>d:N</c> 為天（≥ 24 小時）；第二個是上下文 tokens 的縮寫。</item>
/// <item>第 4 條 <c>big-file</c>：<c>[檔名, "37K"]</c>，Key 為檔名。檔名是 <see cref="ContextBlock.DisplayName"/>；第二個是檔案 tokens 的縮寫。</item>
/// <item>第 5 條 <c>images</c>：<c>["4"]</c>（前文裡還沒被摘要整理掉的圖片張數）。</item>
/// <item>第 6 條 <c>mcp-tools</c>：<c>["14K"]</c>（工具定義 tokens 的縮寫）。</item>
/// <item>第 8 條 <c>long-output</c>：<c>[]</c>。</item>
/// <item>第 9 條 <c>repeat-paste</c>：<c>["3"]</c>（第一次出現的回合數，第幾則使用者訊息，從 1 起）。</item>
/// </list>
/// </summary>
public static class UsageHintRules
{
    private static readonly Regex ParagraphBreak = new(@"\n\s*\n");
    private static readonly Regex Whitespace = new(@"\s+");

    /// <summary>
    /// 回應結束後要檢查的規則（第 2、4、5、6、8、9 條），依優先順序排列。沒有符合時回空清單。
    /// </summary>
    public static IReadOnlyList<UsageHintMatch> AfterResponse(HintInput input)
    {
        var matches = new UsageHintMatch?[]
        {
            LongConversation(input),
            BigFile(input),
            Images(input),
            McpTools(input),
            LongOutput(input),
            RepeatPaste(input),
        };
        return matches.OfType<UsageHintMatch>().ToList();
    }

    /// <summary>
    /// 第 3 條 <c>stale-resume</c>：打開或切回一則對話時判斷（輸入框取得焦點時）。
    /// <paramref name="lastAssistant"/> 是這則對話最後一則助理訊息；沒有時回 null。
    /// </summary>
    public static UsageHintMatch? OnResume(ChatMessage? lastAssistant, int contextUsed, DateTimeOffset now)
    {
        if (lastAssistant is null || contextUsed < UsageHintThresholds.StaleResumeContextTokens)
            return null;

        var idle = now - lastAssistant.CreatedAt;
        if (idle < UsageHintThresholds.StaleResumeIdle)
            return null;

        var elapsed = idle < TimeSpan.FromHours(24)
            ? "h:" + Math.Max(1, (int)idle.TotalHours).ToString(CultureInfo.InvariantCulture)
            : "d:" + ((int)idle.TotalDays).ToString(CultureInfo.InvariantCulture);
        return new UsageHintMatch(UsageHintIds.StaleResume, [elapsed, MoneyFormat.Tokens(contextUsed)]);
    }

    /// <summary>第 2 條：上下文 ≥ 上限的 60%，或回合數 ≥ 40 且最近 10 則有用量的助理訊息平均輸入（含快取）≥ 60K。</summary>
    private static UsageHintMatch? LongConversation(HintInput input)
    {
        var nearLimit = input.ContextLimit > 0
            && input.ContextUsed >= input.ContextLimit * UsageHintThresholds.LongConversationContextRatio;
        if (nearLimit)
            return new UsageHintMatch(UsageHintIds.LongConversation, []);

        if (input.TurnCount < UsageHintThresholds.LongConversationTurns)
            return null;

        // 最近 10 則有 Usage 的助理訊息；不足 10 則時不判斷（平均值太不穩）
        var recent = input.History
            .Where(m => m.Role == ChatRole.Assistant && m.Usage is not null)
            .TakeLast(UsageHintThresholds.LongConversationRecentRequests)
            .ToList();
        if (recent.Count < UsageHintThresholds.LongConversationRecentRequests)
            return null;

        var average = recent.Average(m => (double)InputSideTokens(m.Usage!));
        return average >= UsageHintThresholds.LongConversationAvgInputTokens
            ? new UsageHintMatch(UsageHintIds.LongConversation, [])
            : null;
    }

    /// <summary>第 4 條：前文裡（還沒被摘要整理掉的）單一上下文檔案 ≥ 15K tokens，且它之後已有 ≥ 20 則助理訊息。多個符合時取最大的。</summary>
    private static UsageHintMatch? BigFile(HintInput input)
    {
        var summarizedUpto = SummarizedUpto(input);
        ContextBlock? best = null;
        var bestTokens = 0;

        foreach (var message in input.History)
        {
            if (message.Role != ChatRole.User || message.Seq <= summarizedUpto)
                continue;

            foreach (var block in message.Content.OfType<ContextBlock>())
            {
                var tokens = ContextEstimator.EstimateText(block.Content);
                if (tokens < UsageHintThresholds.BigFileTokens || tokens <= bestTokens)
                    continue;
                if (AssistantsAfter(input.History, message.Seq) < UsageHintThresholds.BigFileRequestsAfter)
                    continue;

                best = block;
                bestTokens = tokens;
            }
        }

        if (best is null)
            return null;
        return new UsageHintMatch(
            UsageHintIds.BigFile,
            [best.DisplayName, MoneyFormat.Tokens(bestTokens)],
            Key: best.DisplayName);
    }

    /// <summary>第 5 條：前文裡（還沒被摘要整理掉的）使用者附上的圖片合計 ≥ 3 張或 ≥ 5K tokens，且最早那張之後已有 ≥ 10 則助理訊息。</summary>
    private static UsageHintMatch? Images(HintInput input)
    {
        var summarizedUpto = SummarizedUpto(input);
        var withImages = input.History
            .Where(m => m.Role == ChatRole.User && m.Seq > summarizedUpto && m.Content.OfType<ImageBlock>().Any())
            .ToList();
        if (withImages.Count == 0)
            return null;

        var count = withImages.Sum(m => m.Content.OfType<ImageBlock>().Count());
        var tokens = count * ContextEstimator.ImageTokens;
        if (count < UsageHintThresholds.ImagesCount && tokens < UsageHintThresholds.ImagesTokens)
            return null;
        if (AssistantsAfter(input.History, withImages[0].Seq) < UsageHintThresholds.ImagesRequestsAfter)
            return null;

        return new UsageHintMatch(UsageHintIds.Images, [count.ToString(CultureInfo.InvariantCulture)]);
    }

    /// <summary>第 6 條：這次請求的工具定義 ≥ 10K tokens（新對話的第一次回應）。7 天限制由畫面層查紀錄判斷。</summary>
    private static UsageHintMatch? McpTools(HintInput input)
    {
        if (!input.IsFirstResponseInConversation || input.LastToolDefinitionTokens < UsageHintThresholds.McpToolDefinitionTokens)
            return null;
        return new UsageHintMatch(UsageHintIds.McpTools, [MoneyFormat.Tokens(input.LastToolDefinitionTokens)]);
    }

    /// <summary>第 8 條：最後一則助理訊息本身是長的程式碼輸出，且整則對話中這樣的助理訊息有 ≥ 3 則。</summary>
    private static UsageHintMatch? LongOutput(HintInput input)
    {
        var last = input.History.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (last is null || !IsLongCodeOutput(last))
            return null;

        var count = input.History.Count(m => m.Role == ChatRole.Assistant && IsLongCodeOutput(m));
        return count >= UsageHintThresholds.LongOutputCount
            ? new UsageHintMatch(UsageHintIds.LongOutput, [])
            : null;
    }

    /// <summary>
    /// 第 9 條：最後一則使用者訊息裡有 ≥ 2K tokens 的段落（以空白行分段），和先前使用者訊息的某段相同（空白正規化後 SHA-256）。
    /// 符合時回最早出現的那一次的回合數（第幾則使用者訊息，從 1 起）。
    /// </summary>
    private static UsageHintMatch? RepeatPaste(HintInput input)
    {
        var users = input.History.Where(m => m.Role == ChatRole.User).ToList();
        if (users.Count < 2)
            return null;

        // 先前使用者訊息的段落 → 第一次出現的回合數
        var firstTurn = new Dictionary<string, int>();
        for (var i = 0; i < users.Count - 1; i++)
        {
            foreach (var hash in LongParagraphHashes(users[i]))
                firstTurn.TryAdd(hash, i + 1);
        }

        int? earliest = null;
        foreach (var hash in LongParagraphHashes(users[^1]))
        {
            if (firstTurn.TryGetValue(hash, out var turn) && (earliest is null || turn < earliest))
                earliest = turn;
        }

        return earliest is { } found
            ? new UsageHintMatch(UsageHintIds.RepeatPaste, [found.ToString(CultureInfo.InvariantCulture)])
            : null;
    }

    private static int SummarizedUpto(HintInput input) => input.Compaction.SummaryUptoSeq ?? 0;

    /// <summary>所有晚於 <paramref name="seq"/> 的助理訊息數。</summary>
    private static int AssistantsAfter(IReadOnlyList<ChatMessage> history, int seq) =>
        history.Count(m => m.Role == ChatRole.Assistant && m.Seq > seq);

    private static bool IsLongCodeOutput(ChatMessage message) =>
        message.Usage is { } usage
        && usage.OutputTokens >= UsageHintThresholds.LongOutputTokens
        && message.Content.OfType<TextBlock>().Any(t => t.Text.Contains("```", StringComparison.Ordinal));

    /// <summary>用量裡的輸入側（含快取讀與快取寫），第 2 條用。</summary>
    private static int InputSideTokens(TokenUsage usage) =>
        usage.InputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;

    /// <summary>訊息文字裡 ≥ 2K tokens 的段落，附正規化後的 SHA-256。</summary>
    private static IEnumerable<string> LongParagraphHashes(ChatMessage message)
    {
        foreach (var text in message.Content.OfType<TextBlock>().Select(t => t.Text))
        {
            foreach (var paragraph in ParagraphBreak.Split(text))
            {
                if (ContextEstimator.EstimateText(paragraph) < UsageHintThresholds.RepeatPasteTokens)
                    continue;

                var normalized = Whitespace.Replace(paragraph, " ").Trim();
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
                yield return hash;
            }
        }
    }
}
