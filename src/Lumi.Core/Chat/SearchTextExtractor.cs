using System.Text;

namespace Lumi.Core.Chat;

/// <summary>
/// 訊息的搜尋文字（存在 <c>messages.search_text</c>）：只取 <see cref="TextBlock"/> 的內容，也就是使用者與助理的文字；
/// 不含工具結果、檔案快照、圖片與 thinking。多個文字區塊以換行接起來，超過 <see cref="MaxLength"/> 字元就截斷（截斷不報錯）。
/// </summary>
public static class SearchTextExtractor
{
    public const int MaxLength = 20_000;

    public static string Extract(IEnumerable<ContentBlock> content)
    {
        var builder = new StringBuilder();
        foreach (var block in content.OfType<TextBlock>())
        {
            if (builder.Length > 0)
            {
                if (builder.Length >= MaxLength)
                    break;
                builder.Append('\n');
            }

            var room = MaxLength - builder.Length;
            builder.Append(block.Text.AsSpan(0, Math.Min(block.Text.Length, room)));
            if (builder.Length >= MaxLength)
                break;
        }

        // 不切開代理對（emoji 等）：截在高代理字元上就去掉它
        if (builder.Length > MaxLength)
            builder.Length = MaxLength;
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
            builder.Length--;
        return builder.ToString();
    }
}
