namespace Lumi.Core.Prompts;

/// <summary>
/// 對話中途 system prompt 的內容變了（模型剛存了記憶、使用者改了人設或記憶開關）時，不改 system，而是把最新的完整內容
/// 包成一個區塊附在那一則訊息的 <see cref="Chat.ChatMessage.TurnSuffix"/> 裡（2026-10-25）。
/// 原因：Anthropic 的 thinking 區塊簽章綁定 system 與它之前的全部內容，system 在同一則對話裡一變，之前的 thinking 區塊就失效、請求 400。
/// system 因此在一則對話內固定（存在對話上），變動只往後加。
/// </summary>
public static class SystemPromptUpdate
{
    private const string Open = "<lumi_system_update>";
    private const string Close = "</lumi_system_update>";
    private const string Header = "程式附加：以下是最新的設定，取代先前的 system prompt（其中的記憶與人設以這裡為準，衝突時一律以這裡為準）。";

    public static string Wrap(string system) => $"{Open}\n{Header}\n{system}\n{Close}";

    /// <summary>從最近的往前找，回傳最後一次更新裡的完整 system；沒有任何更新時回 null。</summary>
    public static string? LastIn(IEnumerable<string?> suffixes)
    {
        foreach (var suffix in suffixes.Reverse())
        {
            if (suffix is null)
                continue;
            var start = suffix.LastIndexOf(Open, StringComparison.Ordinal);
            if (start < 0)
                continue;
            var body = start + Open.Length + 1 + Header.Length + 1;
            var end = suffix.LastIndexOf(Close, StringComparison.Ordinal);
            if (end >= body + 1)
                return suffix[body..(end - 1)];
        }
        return null;
    }
}
