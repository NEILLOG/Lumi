using Lumi.Core.Chat;

namespace Lumi.Core.Branches;

/// <summary>支線放進訊息裡的兩種區塊（出處、帶回的摘要），以及標題、引用、摘錄的整理（計畫 6.4）。</summary>
public static class BranchBlocks
{
    /// <summary>支線第一則訊息的出處區塊（6.4 的格式，內文已經包好 &lt;branch_start&gt;）。</summary>
    public static ContextBlock Origin(Branch branch, TimeZoneInfo zone) => throw new NotImplementedException("TODO(36B)");

    /// <summary>帶回的摘要區塊：DisplayName＝支線標題、Path＝支線 id、Content＝摘要原文（送出時才包，見 ContextBlockFormatter）。</summary>
    public static ContextBlock Summary(Branch branch, string title) => throw new NotImplementedException("TODO(36B)");

    /// <summary>訊息裡全部文字區塊接起來、連續的空白（含換行）縮成一個空白、去頭尾，取前 <paramref name="maxChars"/> 字（多的不加省略號）。</summary>
    public static string ExcerptOf(ChatMessage message, int maxChars) => throw new NotImplementedException("TODO(36B)");

    /// <summary>選取的文字：空白縮成一個、去頭尾；超過 <see cref="BranchLimits.QuoteMaxChars"/> 截斷加「…」；空的回 null。</summary>
    public static string? NormalizeQuote(string? quote) => throw new NotImplementedException("TODO(36B)");

    /// <summary>標題：有選取的文字取前 <see cref="BranchLimits.TitleFromQuoteChars"/> 字（超過加「…」），沒有就是 <see cref="BranchLimits.DefaultTitle"/>。</summary>
    public static string TitleFromQuote(string? quote) => throw new NotImplementedException("TODO(36B)");
}
