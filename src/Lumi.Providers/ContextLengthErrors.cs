namespace Lumi.Providers;

/// <summary>辨認「請求超過模型上下文長度」的錯誤（M22）：兩家供應商的 400／413 內文用字不同，但都含下面其中一句。</summary>
internal static class ContextLengthErrors
{
    private static readonly string[] Markers =
    [
        "context_length_exceeded",
        "maximum context length",
        "prompt is too long",
        "too many tokens",
    ];

    public const string Message = "這則對話超過模型能處理的長度。";

    public static bool Matches(string? errorText) =>
        errorText is { Length: > 0 } && Markers.Any(m => errorText.Contains(m, StringComparison.OrdinalIgnoreCase));
}
