namespace Lumi.Documents;

/// <summary>切片結果。<see cref="NextOffset"/> 有值時表示 <see cref="Content"/> 之後還有內容，下次從這個位移續讀。</summary>
internal readonly record struct OutputSlice(string Content, bool Truncated, int? NextOffset);

/// <summary>把完整輸出依位移與上限切出一段（字元為 UTF-16 code unit，但不會把代理對切成兩半）。</summary>
internal static class OutputWindow
{
    public const int DefaultMaxChars = 8000;
    public const int MaxCharsLimit = 16000;

    /// <param name="offset">從第幾個字元開始（從 0 算）。負數視為 0；超過結尾回空字串、沒有續讀位置。</param>
    /// <param name="maxChars">上限；0 或負數用預設值，超過 <see cref="MaxCharsLimit"/> 截到上限。</param>
    public static OutputSlice Slice(string text, int offset, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);

        var limit = maxChars <= 0 ? DefaultMaxChars : Math.Min(maxChars, MaxCharsLimit);
        var start = Math.Max(0, offset);

        if (start >= text.Length)
            return new OutputSlice("", false, null);

        // 起點落在低代理字元上（呼叫端給了錯的位移）：跳過它，免得輸出以孤立代理字元開頭
        if (char.IsLowSurrogate(text[start]) && start > 0 && char.IsHighSurrogate(text[start - 1]))
            start++;
        if (start >= text.Length)
            return new OutputSlice("", false, null);

        var end = Math.Min(text.Length, start + limit);

        // 終點若切在代理對中間，往回退一格；若退完沒有任何字元（maxChars 太小），改為包含整個代理對
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
        {
            if (end - 1 > start)
                end--;
            else
                end = Math.Min(text.Length, start + 2);
        }

        var truncated = end < text.Length;
        return new OutputSlice(text[start..end], truncated, truncated ? end : null);
    }
}
