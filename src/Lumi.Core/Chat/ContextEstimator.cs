namespace Lumi.Core.Chat;

/// <summary>
/// 估算下一次請求會有多少 token（上下文壓縮的觸發用，不需要精確）。
/// 有最近一則助理訊息回報的用量時：那次請求的輸入（含快取）＋輸出，加上它之後新增訊息的估算；沒有時全部用字元估算。
/// 字元估算：每 3 個字元 1 個 token；中日韓文字每個字 1 個 token（只用 ÷3 會把中文低估一半以上）。
/// </summary>
public static class ContextEstimator
{
    /// <summary>一張圖片的粗估。</summary>
    public const int ImageTokens = 1500;

    /// <summary>
    /// <paramref name="applied"/> 是套用整理後的歷史（<see cref="CompactionApplier.Apply"/>）；
    /// <paramref name="overheadTokens"/> 是 system prompt 與工具定義（沒有用量資料可用時才加，用量已經包含它們）。
    /// </summary>
    public static int Estimate(IReadOnlyList<ChatMessage> applied, CompactionState state, int overheadTokens)
    {
        // 整理之前的用量已經不代表現在的請求大小：只認整理之後才產生的助理訊息
        var anchor = -1;
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            if (applied[i].Role == ChatRole.Assistant && applied[i].Usage is not null && applied[i].Seq > state.AppliedAtSeq)
            {
                anchor = i;
                break;
            }
        }

        if (anchor < 0)
            return overheadTokens + applied.Sum(EstimateMessage);

        var usage = applied[anchor].Usage!;
        var reported = usage.InputTokens + usage.CacheCreationInputTokens + usage.CacheReadInputTokens + usage.OutputTokens;
        return reported + applied.Skip(anchor + 1).Sum(EstimateMessage);
    }

    public static int EstimateMessage(ChatMessage message)
    {
        var tokens = 4;   // 每則訊息的固定開銷
        foreach (var block in message.Content)
        {
            tokens += block switch
            {
                TextBlock t => EstimateText(t.Text),
                ThinkingBlock t => EstimateText(t.Thinking),
                ToolUseBlock u => EstimateText(u.Name) + EstimateText(u.InputJson),
                ToolResultBlock r => EstimateText(r.Content) + (r.Images?.Count ?? 0) * ImageTokens,
                ContextBlock c => EstimateText(c.Content ?? "") + EstimateText(c.Path),
                ImageBlock => ImageTokens,
                ProviderBlock p => EstimateText(p.RawJson),
                _ => 0,
            };
        }
        return tokens + (message.TurnSuffix is { } suffix ? EstimateText(suffix) : 0);
    }

    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var cjk = 0;
        foreach (var c in text)
        {
            if (IsCjk(c))
                cjk++;
        }
        return cjk + (text.Length - cjk + 2) / 3;
    }

    private static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿'     // 部首、注音、日文假名、CJK 符號與統一表意文字
        or >= '가' and <= '힯'       // 韓文音節
        or >= '豈' and <= '﫿'       // 相容表意文字
        or >= '＀' and <= '￯';      // 全形
}
