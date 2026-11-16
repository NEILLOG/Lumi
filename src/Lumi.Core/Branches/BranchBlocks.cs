using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.Core.Branches;

/// <summary>支線放進訊息裡的兩種區塊（出處、帶回的摘要），以及標題、引用、摘錄的整理（計畫 6.4）。</summary>
public static class BranchBlocks
{
    private const string OriginTimeFormat = "yyyy/MM/dd HH:mm";
    private const string OriginHeader =
        "使用者從上面的對話開了一條「支線」：暫時離開目前的話題，問一個相關的問題。之後 Lumi 會把這條支線整理成摘要，帶回原本的對話。\n"
        + "- 專心回答使用者在支線裡問的事；不要接著做原本對話還沒做完的事，也不要主動把話題帶回去。\n"
        + "- 使用者常常是遇到看不懂的名詞或背景知識：先用白話講清楚，需要時再補細節。";
    private const string EscapedOriginClose = "&lt;/branch_start";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>支線第一則訊息的出處區塊（6.4 的格式，內文已經包好 &lt;branch_start&gt;）。</summary>
    public static ContextBlock Origin(Branch branch, TimeZoneInfo zone)
    {
        var at = TimeZoneInfo.ConvertTime(branch.ForkMessageAt, zone).ToString(OriginTimeFormat, CultureInfo.InvariantCulture);
        var excerpt = Escape(Cut(branch.ForkExcerpt, BranchLimits.OriginExcerptChars));

        var content = new StringBuilder();
        content.Append("<branch_start>\n").Append(OriginHeader).Append('\n');
        content.Append("分出來的位置：你在 ").Append(at).Append(" 的回答，開頭是「").Append(excerpt).Append("」\n");
        if (!string.IsNullOrWhiteSpace(branch.Quote))
            content.Append("使用者選取的文字：「").Append(Escape(branch.Quote)).Append("」\n");
        content.Append("</branch_start>");

        return new ContextBlock(ContextKind.BranchOrigin, "branch_origin", branch.ForkMessageId, content.ToString(), true);
    }

    /// <summary>帶回的摘要區塊：DisplayName＝支線標題、Path＝支線 id、Content＝摘要原文（送出時才包，見 ContextBlockFormatter）。</summary>
    public static ContextBlock Summary(Branch branch, string title) =>
        new(ContextKind.BranchSummary, title, branch.ConversationId, branch.Summary, true);

    /// <summary>訊息裡全部文字區塊接起來、連續的空白（含換行）縮成一個空白、去頭尾，取前 <paramref name="maxChars"/> 字（多的不加省略號）。</summary>
    public static string ExcerptOf(ChatMessage message, int maxChars)
    {
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text));
        return Cut(Collapse(text), maxChars);
    }

    /// <summary>選取的文字：空白縮成一個、去頭尾；超過 <see cref="BranchLimits.QuoteMaxChars"/> 截斷加「…」；空的回 null。</summary>
    public static string? NormalizeQuote(string? quote)
    {
        var text = Collapse(quote);
        if (text.Length == 0)
            return null;
        return text.Length > BranchLimits.QuoteMaxChars ? Cut(text, BranchLimits.QuoteMaxChars) + "…" : text;
    }

    /// <summary>標題：有選取的文字取前 <see cref="BranchLimits.TitleFromQuoteChars"/> 字（超過加「…」），沒有就是 <see cref="BranchLimits.DefaultTitle"/>。</summary>
    public static string TitleFromQuote(string? quote)
    {
        var text = NormalizeQuote(quote);
        if (text is null)
            return BranchLimits.DefaultTitle;
        return text.Length > BranchLimits.TitleFromQuoteChars ? Cut(text, BranchLimits.TitleFromQuoteChars) + "…" : text;
    }

    /// <summary>連續空白縮成一個並去頭尾；null 當空字串。</summary>
    private static string Collapse(string? text) => Whitespace.Replace(text ?? "", " ").Trim();

    /// <summary>取前 <paramref name="max"/> 字；切在代理對中間時少取一個字，不切開字元。</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var keep = max;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
            keep--;
        return text[..keep];
    }

    /// <summary>內文裡的結尾標籤跳脫，不讓它提早結束 <c>&lt;branch_start&gt;</c> 區塊。</summary>
    private static string Escape(string text) =>
        text.Replace("</branch_start", EscapedOriginClose, StringComparison.OrdinalIgnoreCase);
}
