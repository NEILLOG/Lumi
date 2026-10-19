using Lumi.Core.Chat;

namespace Lumi.Core.Context;

/// <summary>
/// 送出前的 token 預估（只是參考，顯示成「≈ N tokens」）。實際用量以供應商回傳的 <c>usage</c> 為準（agent.md 第 7 節）。
/// </summary>
public interface ITokenEstimator
{
    int Estimate(string text);

    int EstimateMessages(IEnumerable<ChatMessage> messages);

    /// <summary>每張圖片的估計值（實際依尺寸而定，這裡取固定值）。</summary>
    int ImageTokens { get; }
}

/// <summary>
/// 啟發式：ASCII 約 4 個字元 1 token、帶重音的拉丁字母約 2 個字元 1 token、中日韓與其他字元約 1 個字 1 token
/// （emoji 的兩個 UTF-16 單元各算 1，所以一個 emoji 約 2 token）。偏保守（寧可高估一點）。
/// </summary>
public sealed class HeuristicTokenEstimator : ITokenEstimator
{
    public int ImageTokens => 1_500;

    public int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        double tokens = 0;
        foreach (var c in text)
        {
            tokens += c switch
            {
                < (char)0x80 => 0.25,
                < (char)0x250 => 0.5,
                _ => 1.0,
            };
        }
        return (int)Math.Ceiling(tokens);
    }

    public int EstimateMessages(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += 4;   // 每則訊息的固定開銷
            foreach (var block in message.Content)
            {
                total += block switch
                {
                    TextBlock t => Estimate(t.Text),
                    ThinkingBlock t => Estimate(t.Thinking),
                    ContextBlock c => Estimate(ContextBlockFormatter.ToPromptText(c)),
                    ImageBlock => ImageTokens,
                    ToolUseBlock u => Estimate(u.Name) + Estimate(u.InputJson),
                    ToolResultBlock r => Estimate(r.Content),
                    _ => 0,
                };
            }
        }
        return total;
    }
}
