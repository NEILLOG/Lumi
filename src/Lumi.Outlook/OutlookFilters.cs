using System.Globalization;
using Lumi.Core.Outlook;

namespace Lumi.Outlook;

/// <summary>Outlook 的篩選字串與日期判斷（純邏輯，不碰 COM）。</summary>
internal static class OutlookFilters
{
    /// <summary>文字條件的 DASL 篩選（<c>@SQL=…</c>）；沒有任何文字條件時回 null。日期不放進篩選。</summary>
    public static string? BuildMailFilter(MailSearchRequest request)
    {
        var query = request.Query?.Trim();
        var from = request.From?.Trim();
        var hasQuery = !string.IsNullOrEmpty(query);
        var hasFrom = !string.IsNullOrEmpty(from);

        if (!hasQuery && !hasFrom && !request.UnreadOnly && !request.HasAttachments)
            return null;

        var parts = new List<string>();
        if (hasQuery)
        {
            var q = EscapeLike(query!);
            parts.Add($"(\"urn:schemas:httpmail:subject\" LIKE '%{q}%' OR \"urn:schemas:httpmail:textdescription\" LIKE '%{q}%')");
        }
        if (hasFrom)
        {
            var f = EscapeLike(from!);
            parts.Add($"(\"urn:schemas:httpmail:fromname\" LIKE '%{f}%' OR \"urn:schemas:httpmail:fromemail\" LIKE '%{f}%')");
        }
        if (request.UnreadOnly)
            parts.Add("\"urn:schemas:httpmail:read\" = 0");
        if (request.HasAttachments)
            parts.Add("\"urn:schemas:httpmail:hasattachment\" = 1");

        return "@SQL=" + string.Join(" AND ", parts);
    }

    /// <summary>收到時間在 [since, until) 之內；null＝不限。</summary>
    public static bool InRange(DateTime received, DateTime? since, DateTime? until) =>
        (since is null || received >= since.Value) && (until is null || received < until.Value);

    /// <summary>比 since 還舊（由新到舊逐封檢查時，遇到就可以停）；since 是 null 時一律 false。</summary>
    public static bool IsOlderThanSince(DateTime received, DateTime? since) =>
        since is not null && received < since.Value;

    /// <summary>行事曆的 Jet 篩選：<c>[Start] &lt; '{end:g}' AND [End] &gt; '{start:g}'</c>，日期用指定的地區設定。</summary>
    public static string BuildCalendarRestrict(DateTime start, DateTime end, CultureInfo culture) =>
        $"[Start] < '{end.ToString("g", culture)}' AND [End] > '{start.ToString("g", culture)}'";

    /// <summary>
    /// 跳脫 LIKE 的萬用字元與 DASL 字串的單引號。順序很重要：先處理 <c>[</c>，
    /// 否則後面加進去的 <c>[%]</c>、<c>[_]</c> 會被再跳脫一次。
    /// </summary>
    private static string EscapeLike(string text) =>
        text.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);
}
