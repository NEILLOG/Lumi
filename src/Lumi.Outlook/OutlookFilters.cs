using System.Globalization;
using Lumi.Core.Outlook;

namespace Lumi.Outlook;

/// <summary>Outlook 的篩選字串與日期判斷（純邏輯，不碰 COM）。</summary>
internal static class OutlookFilters
{
    /// <summary>文字條件的 DASL 篩選（<c>@SQL=…</c>）；沒有任何文字條件時回 null。日期不放進篩選。</summary>
    public static string? BuildMailFilter(MailSearchRequest request) => throw new NotImplementedException(); // TODO(30A)

    /// <summary>收到時間在 [since, until) 之內；null＝不限。</summary>
    public static bool InRange(DateTime received, DateTime? since, DateTime? until) => throw new NotImplementedException(); // TODO(30A)

    /// <summary>比 since 還舊（由新到舊逐封檢查時，遇到就可以停）；since 是 null 時一律 false。</summary>
    public static bool IsOlderThanSince(DateTime received, DateTime? since) => throw new NotImplementedException(); // TODO(30A)

    /// <summary>行事曆的 Jet 篩選：<c>[Start] &lt; '{end:g}' AND [End] &gt; '{start:g}'</c>，日期用指定的地區設定。</summary>
    public static string BuildCalendarRestrict(DateTime start, DateTime end, CultureInfo culture) => throw new NotImplementedException(); // TODO(30A)
}
