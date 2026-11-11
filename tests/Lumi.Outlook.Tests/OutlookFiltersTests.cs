using System.Globalization;
using Lumi.Core.Outlook;

namespace Lumi.Outlook.Tests;

public class OutlookFiltersTests
{
    private static MailSearchRequest Request(string? query = null, string? from = null, DateTime? since = null,
        DateTime? until = null, bool unread = false, bool attachments = false) =>
        new(query, from, OutlookFolder.Inbox, since, until, unread, attachments, 20);

    [Fact]
    public void Mail_filter_escapes_quote_and_wildcards()
    {
        var filter = OutlookFilters.BuildMailFilter(Request(query: "O'Neil 50%_[x]"));

        Assert.NotNull(filter);
        Assert.Contains("O''Neil 50[%][_][[]x]", filter);
    }

    [Fact]
    public void Mail_filter_combines_conditions_with_AND()
    {
        var filter = OutlookFilters.BuildMailFilter(Request(query: "專案", from: "Amy", unread: true, attachments: true));

        Assert.NotNull(filter);
        Assert.StartsWith("@SQL=", filter);
        Assert.Equal(3, filter.Split(" AND ").Length - 1);
        Assert.Contains("urn:schemas:httpmail:subject\" LIKE '%專案%'", filter);
        Assert.Contains("urn:schemas:httpmail:fromname\" LIKE '%Amy%'", filter);
        Assert.Contains("\"urn:schemas:httpmail:read\" = 0", filter);
        Assert.Contains("\"urn:schemas:httpmail:hasattachment\" = 1", filter);
    }

    [Fact]
    public void No_text_conditions_returns_null()
    {
        var since = new DateTime(2026, 1, 1);
        var until = new DateTime(2026, 2, 1);

        Assert.Null(OutlookFilters.BuildMailFilter(Request(since: since, until: until)));
        Assert.Null(OutlookFilters.BuildMailFilter(Request(query: "   ", from: "")));
    }

    [Fact]
    public void Unread_only_alone_builds_filter()
    {
        var filter = OutlookFilters.BuildMailFilter(Request(unread: true));

        Assert.Equal("@SQL=\"urn:schemas:httpmail:read\" = 0", filter);
    }

    [Fact]
    public void In_range_is_inclusive_since_exclusive_until()
    {
        var since = new DateTime(2026, 3, 1);
        var until = new DateTime(2026, 3, 8);

        Assert.True(OutlookFilters.InRange(since, since, until));
        Assert.True(OutlookFilters.InRange(new DateTime(2026, 3, 7, 23, 59, 0), since, until));
        Assert.False(OutlookFilters.InRange(until, since, until));
        Assert.False(OutlookFilters.InRange(new DateTime(2026, 2, 28), since, until));
        Assert.True(OutlookFilters.InRange(new DateTime(2000, 1, 1), null, null));
    }

    [Fact]
    public void Older_than_since()
    {
        var since = new DateTime(2026, 3, 1);

        Assert.True(OutlookFilters.IsOlderThanSince(new DateTime(2026, 2, 28), since));
        Assert.False(OutlookFilters.IsOlderThanSince(since, since));
        Assert.False(OutlookFilters.IsOlderThanSince(new DateTime(2000, 1, 1), null));
    }

    [Fact]
    public void Calendar_restrict_uses_given_culture()
    {
        var start = new DateTime(2026, 11, 12, 9, 0, 0);
        var end = new DateTime(2026, 11, 12, 15, 30, 0);
        var zh = new CultureInfo("zh-TW");
        var en = new CultureInfo("en-US");

        var zhResult = OutlookFilters.BuildCalendarRestrict(start, end, zh);
        var enResult = OutlookFilters.BuildCalendarRestrict(start, end, en);

        Assert.Equal($"[Start] < '{end.ToString("g", zh)}' AND [End] > '{start.ToString("g", zh)}'", zhResult);
        Assert.Equal($"[Start] < '{end.ToString("g", en)}' AND [End] > '{start.ToString("g", en)}'", enResult);
        Assert.NotEqual(zhResult, enResult);
    }
}
