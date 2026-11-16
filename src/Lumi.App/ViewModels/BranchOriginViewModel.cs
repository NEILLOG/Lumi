using System.Globalization;
using Lumi.App.Resources;
using Lumi.Core.Branches;

namespace Lumi.App.ViewModels;

/// <summary>
/// 支線訊息列表最上方的出處卡片（M36，4.8）：「從主線（或「第一層標題」）分出來 · Lumi {時間} 的回答」，
/// 下面是分岔那則回答的開頭；有選取的文字就把它標出來，在開頭裡找不到時另外寫「你選的：」。
/// 只是畫面上的標記，不送給模型（送給模型的是 <c>BranchOrigin</c> 區塊）。
/// </summary>
public partial class BranchOriginViewModel : MessageViewModel
{
    public BranchOriginViewModel(BranchOriginInfo info) : base(isUser: false)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;

        var time = info.ForkMessageAt.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        Eyebrow = info.ParentTitle is null
            ? string.Format(CultureInfo.CurrentCulture, Strings.Branch_OriginFromMainFormat, time)
            : string.Format(CultureInfo.CurrentCulture, Strings.Branch_OriginFromBranchFormat, info.ParentTitle, time);

        var window = info.ForkExcerpt.Length > BranchLimits.OriginExcerptChars
            ? info.ForkExcerpt[..BranchLimits.OriginExcerptChars]
            : info.ForkExcerpt;
        var index = FindQuote(window, info.Quote, out var quote);
        if (index >= 0 && quote is not null)
        {
            ExcerptBefore = Ellipsis(window[..index], leading: true);
            ExcerptQuote = window.Substring(index, quote.Length);
            ExcerptAfter = Ellipsis(window[(index + quote.Length)..], leading: false);
            ExtraQuoteText = null;
        }
        else
        {
            ExcerptBefore = Ellipsis(window, leading: true, trailing: true);
            ExcerptQuote = "";
            ExcerptAfter = "";
            ExtraQuoteText = info.Quote is null
                ? null
                : string.Format(CultureInfo.CurrentCulture, Strings.Branch_OriginQuoteFormat, info.Quote);
        }
    }

    public BranchOriginInfo Info { get; }

    /// <summary>第一行：「從主線分出來 · Lumi 11/16 10:32 的回答」（第二層是「從「{標題}」分出來 · …」）。</summary>
    public string Eyebrow { get; }

    /// <summary>引用前面的文字（前面有「…」）。沒有標出來的時候是整段開頭。</summary>
    public string ExcerptBefore { get; }

    /// <summary>標出來的選取文字；沒有標出來時是空字串。</summary>
    public string ExcerptQuote { get; }

    /// <summary>引用後面的文字（後面有「…」）。</summary>
    public string ExcerptAfter { get; }

    /// <summary>開頭裡找不到選取的文字時，另外一行「你選的：「…」」；其他情況是 null。</summary>
    public string? ExtraQuoteText { get; }

    /// <summary>有標出選取的文字（畫成底色）。</summary>
    public bool HasHighlight => ExcerptQuote.Length > 0;

    public override bool IsAssistant => false;

    public override bool IsMarker => true;

    /// <summary>在開頭裡找選取的文字；找不到時試著去掉結尾的「…」（選取被截斷過）。回傳 -1 表示找不到。</summary>
    private static int FindQuote(string window, string? quote, out string? found)
    {
        found = null;
        if (string.IsNullOrEmpty(quote))
            return -1;

        var index = window.IndexOf(quote, StringComparison.Ordinal);
        if (index >= 0)
        {
            found = quote;
            return index;
        }

        if (quote.EndsWith('…') && quote.Length > 1)
        {
            var trimmed = quote[..^1];
            index = window.IndexOf(trimmed, StringComparison.Ordinal);
            if (index >= 0)
            {
                found = trimmed;
                return index;
            }
        }
        return -1;
    }

    private static string Ellipsis(string text, bool leading, bool trailing = false)
    {
        if (text.Length == 0)
            return "";
        return (leading ? "…" : "") + text + (trailing || !leading ? "…" : "");
    }
}
