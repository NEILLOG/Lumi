namespace Lumi.Core.Chat;

/// <summary>
/// 搜尋結果的命中片段：命中字前後各約 <see cref="Context"/> 個字，換行與連續空白收成一個空白，只占一行。
/// </summary>
public static class SearchSnippet
{
    public const int Context = 20;

    /// <summary>在 <paramref name="text"/> 裡找 <paramref name="query"/>（不分大小寫）並擷取片段；找不到時從開頭取。</summary>
    public static string Create(string text, string query)
    {
        if (text.Length == 0)
            return string.Empty;

        var index = query.Length == 0 ? -1 : text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            index = 0;

        var start = Math.Max(0, index - Context);
        var end = Math.Min(text.Length, index + query.Length + Context);
        // 不切開代理對
        if (start > 0 && char.IsLowSurrogate(text[start]))
            start++;
        if (end < text.Length && end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;

        var piece = text.AsSpan(start, Math.Max(0, end - start)).ToString();
        return string.Join(' ', piece.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
