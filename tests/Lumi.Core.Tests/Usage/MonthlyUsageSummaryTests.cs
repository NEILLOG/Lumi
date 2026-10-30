using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>月報的計算。所有時間都在 Asia/Taipei（UTC+8）下判斷，跨月邊界用 UTC 時間比較容易看錯，所以測試直接寫本地時間。</summary>
public sealed class MonthlyUsageSummaryTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    /// <summary>2026 年的本地時間（台北）。</summary>
    private static DateTimeOffset At(int month, int day, int hour = 12, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    private static UsageRecord Rec(DateTimeOffset at, decimal? cost, bool estimated = false) =>
        new(UsageRecord.NewId(), at, "conv", UsagePurpose.Chat, ChatMode.Workbench, "ms", "model", "conn",
            TokenUsage.Zero, estimated, null, cost, 0);

    /// <summary>10 月 1 日到 <paramref name="lastDay"/> 每天一筆 1 美元。</summary>
    private static List<UsageRecord> OneDollarPerDay(int lastDay) =>
        Enumerable.Range(1, lastDay).Select(d => Rec(At(10, d), 1m)).ToList();

    [Fact]
    public void Build_CountsOnlyLocalCurrentMonth()
    {
        var records = new List<UsageRecord>
        {
            Rec(new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.FromHours(8)), 100m), // 九月
            Rec(At(10, 1, 0, 30), 2m),                                                  // 十月一日 00:30（台北）
            Rec(At(10, 2), 3m),
            Rec(new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(8)), 4m), // 十月最後一天
        };

        var summary = MonthlyUsageSummary.Build(records, null, At(10, 2), Taipei);

        Assert.Equal(9m, summary.TotalUsd);
        Assert.Equal(31, summary.Daily.Count);
        Assert.Equal(2m, summary.Daily[0].Usd);
        Assert.Equal(3m, summary.Daily[1].Usd);
        Assert.Equal(4m, summary.Daily[30].Usd);
    }

    [Fact]
    public void MonthRange_IsLocalMonth_ExpressedInUtc()
    {
        // UTC 的 9/30 17:00 已經是台北的 10/1 01:00，所以月份是十月。
        var (from, to) = MonthlyUsageSummary.MonthRange(new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero), Taipei);

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), to);
    }

    [Fact]
    public void RangeStart_IsFirstRecordDay_WhenLaterThanMonthStart()
    {
        var records = new List<UsageRecord> { Rec(At(10, 5), 1m), Rec(At(10, 3), 1m) };

        var summary = MonthlyUsageSummary.Build(records, null, At(10, 6), Taipei);

        Assert.Equal(new DateOnly(2026, 10, 3), summary.RangeStart);
        Assert.Equal(new DateOnly(2026, 10, 6), summary.RangeEnd);
    }

    [Fact]
    public void RangeStart_IsMonthStart_WhenNoRecords()
    {
        var summary = MonthlyUsageSummary.Build([], null, At(10, 6), Taipei);

        Assert.Equal(new DateOnly(2026, 10, 1), summary.RangeStart);
        Assert.True(summary.IsEmpty);
        Assert.Equal(0m, summary.TotalUsd);
        Assert.Null(summary.ProjectedUsd);
        Assert.Equal(31, summary.Daily.Count);
    }

    [Fact]
    public void Projection_IsNull_UntilSevenRecordDays()
    {
        Assert.Null(MonthlyUsageSummary.Build(OneDollarPerDay(6), null, At(10, 6), Taipei).ProjectedUsd);

        var seventh = MonthlyUsageSummary.Build(OneDollarPerDay(7), null, At(10, 7), Taipei);
        Assert.Equal(31m, seventh.ProjectedUsd); // 7 ÷ 7 × 31
    }

    [Fact]
    public void Projection_DividesByDaysSinceRangeStart_NotMonthStart()
    {
        // 十月 4 日開始有紀錄，到 10 日為止：已過 7 天（4 到 10），不是 10 天。
        var records = Enumerable.Range(4, 7).Select(d => Rec(At(10, d), 1m)).ToList();

        var summary = MonthlyUsageSummary.Build(records, null, At(10, 10), Taipei);

        Assert.Equal(31m, summary.ProjectedUsd);
    }

    [Fact]
    public void Daily_CoversWholeMonth_AndMarksTodayAndFuture()
    {
        var summary = MonthlyUsageSummary.Build(OneDollarPerDay(10), null, At(10, 10), Taipei);

        Assert.Equal(new DateOnly(2026, 10, 1), summary.Daily[0].Date);
        Assert.Equal(new DateOnly(2026, 10, 31), summary.Daily[30].Date);
        Assert.True(summary.Daily[9].IsToday);
        Assert.False(summary.Daily[8].IsToday);
        Assert.False(summary.Daily[9].IsFuture);
        Assert.True(summary.Daily[10].IsFuture);
        Assert.True(summary.Daily[30].IsFuture);
        Assert.Equal(0m, summary.Daily[10].Usd);
    }

    [Fact]
    public void Budget_IsNullWhenNotSet_AndRatioWhenSet()
    {
        var records = new List<UsageRecord> { Rec(At(10, 2), 12.5m) };

        var none = MonthlyUsageSummary.Build(records, null, At(10, 2), Taipei);
        Assert.Null(none.BudgetUsd);
        Assert.Null(none.BudgetRatio);
        Assert.False(none.WillExceed);

        var zero = MonthlyUsageSummary.Build(records, 0m, At(10, 2), Taipei);
        Assert.Null(zero.BudgetRatio);

        var set = MonthlyUsageSummary.Build(records, 25m, At(10, 2), Taipei);
        Assert.Equal(25m, set.BudgetUsd);
        Assert.Equal(0.5, set.BudgetRatio);
    }

    [Fact]
    public void WillExceed_ComparesProjectionWithBudget()
    {
        var records = OneDollarPerDay(7); // 預測 31

        Assert.True(MonthlyUsageSummary.Build(records, 30m, At(10, 7), Taipei).WillExceed);
        Assert.False(MonthlyUsageSummary.Build(records, 31m, At(10, 7), Taipei).WillExceed);
        Assert.False(MonthlyUsageSummary.Build(records, 40m, At(10, 7), Taipei).WillExceed);

        // 紀錄不到 7 天，沒有預測，不會判斷超過。
        Assert.False(MonthlyUsageSummary.Build(OneDollarPerDay(3), 1m, At(10, 3), Taipei).WillExceed);
    }

    [Fact]
    public void Unpriced_AndEstimated_AreCounted_TotalExcludesUnpriced()
    {
        var records = new List<UsageRecord>
        {
            Rec(At(10, 1), 2m),
            Rec(At(10, 2), null),
            Rec(At(10, 3), 1m, estimated: true),
        };

        var summary = MonthlyUsageSummary.Build(records, null, At(10, 3), Taipei);

        Assert.Equal(3m, summary.TotalUsd);
        Assert.Equal(1, summary.UnpricedCount);
        Assert.True(summary.HasEstimated);
        Assert.False(summary.IsEmpty);
    }

    [Fact]
    public void HasEstimated_IsFalse_WhenAllExact()
    {
        var summary = MonthlyUsageSummary.Build([Rec(At(10, 1), 2m)], null, At(10, 1), Taipei);

        Assert.False(summary.HasEstimated);
    }
}
