namespace Lumi.Core.Usage;

/// <summary>一天的費用（本地日期）。<see cref="IsFuture"/> 的日子不畫；0 元的日子畫短條。</summary>
public sealed record DailyCost(DateOnly Date, decimal Usd, bool IsToday, bool IsFuture);

/// <summary>
/// 本月的用量與費用月報（設定 › 用量與費用、工作台用量面板、設定首頁卡片共用的數字）。純邏輯，不碰資料庫。
/// 所有「日」都是本地時區的日期。
/// </summary>
public sealed record MonthlyUsageSummary
{
    /// <summary>有單價的請求加總（未設定單價的不計）。</summary>
    public decimal TotalUsd { get; init; }

    /// <summary>本月有任何一筆是估算值（顯示「≈」）。</summary>
    public bool HasEstimated { get; init; }

    /// <summary>沒有單價、因此沒計入的請求數。</summary>
    public int UnpricedCount { get; init; }

    /// <summary>本月 1 日與本月第一筆紀錄的本地日期取較晚者；沒有紀錄時是 1 日。</summary>
    public DateOnly RangeStart { get; init; }

    /// <summary>今天（本地日期）。</summary>
    public DateOnly RangeEnd { get; init; }

    /// <summary>每月預算（美元）；沒設定回 null。</summary>
    public decimal? BudgetUsd { get; init; }

    /// <summary>已用比例（1.0 ＝ 100%）；沒設預算回 null。</summary>
    public double? BudgetRatio { get; init; }

    /// <summary>
    /// 月底預測（美元）＝ 本月累計 ÷ 已過天數 × 當月天數。本月有紀錄的天數 &lt; 7 時回 null（樣本太少，不預測）。
    /// 已過天數 = <see cref="RangeStart"/> 到今天（含）的天數。
    /// </summary>
    public decimal? ProjectedUsd { get; init; }

    /// <summary>有預算且預測超過預算。</summary>
    public bool WillExceed { get; init; }

    /// <summary>本月沒有任何紀錄。</summary>
    public bool IsEmpty { get; init; }

    /// <summary>從 1 日到月底，每天一筆。</summary>
    public IReadOnlyList<DailyCost> Daily { get; init; } = [];

    /// <summary>
    /// 算本月的月報。<paramref name="records"/> 可以包含其他月份的紀錄（會被略過）。
    /// <paramref name="budgetUsd"/> ≤ 0 視為不設。
    /// </summary>
    public static MonthlyUsageSummary Build(IReadOnlyList<UsageRecord> records, decimal? budgetUsd, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = LocalDate(now, zone);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(today.Year, today.Month);

        var month = records
            .Select(r => (Record: r, Local: LocalDate(r.CreatedAt, zone)))
            .Where(x => x.Local.Year == today.Year && x.Local.Month == today.Month)
            .OrderBy(x => x.Record.CreatedAt)
            .ToList();

        decimal total = 0;
        var perDay = new decimal[daysInMonth];
        foreach (var (record, local) in month)
        {
            if (record.CostUsd is not { } cost)
                continue;
            total += cost;
            perDay[local.Day - 1] += cost;
        }

        var daily = Enumerable.Range(1, daysInMonth)
            .Select(day =>
            {
                var date = new DateOnly(today.Year, today.Month, day);
                return new DailyCost(date, perDay[day - 1], date == today, date > today);
            })
            .ToList();

        var rangeStart = month.Count == 0 ? monthStart : month[0].Local;
        var elapsedDays = today.DayNumber - rangeStart.DayNumber + 1;
        var daysWithRecords = month.Select(x => x.Local).Distinct().Count();
        var projected = daysWithRecords >= 7 && elapsedDays >= 1
            ? total / elapsedDays * daysInMonth
            : (decimal?)null;

        decimal? budget = budgetUsd is decimal limit && limit > 0 ? limit : null;

        return new MonthlyUsageSummary
        {
            TotalUsd = total,
            HasEstimated = month.Any(x => x.Record.Estimated),
            UnpricedCount = month.Count(x => x.Record.CostUsd is null),
            RangeStart = rangeStart,
            RangeEnd = today,
            BudgetUsd = budget,
            BudgetRatio = budget is { } b ? (double)(total / b) : null,
            ProjectedUsd = projected,
            WillExceed = budget is { } cap && projected is { } p && p > cap,
            IsEmpty = month.Count == 0,
            Daily = daily,
        };
    }

    /// <summary>本地時區的本月：月初 00:00 到下月初 00:00，轉成 UTC（給 <c>IUsageStore</c> 查詢用）。</summary>
    public static (DateTimeOffset From, DateTimeOffset To) MonthRange(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return (ToUtc(start, zone), ToUtc(start.AddMonths(1), zone));
    }

    /// <summary>本地日期（給 <c>app_meta</c> 的月份鍵與每日分組共用）。</summary>
    internal static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static DateTimeOffset ToUtc(DateTime localMidnight, TimeZoneInfo zone) =>
        new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight)).ToUniversalTime();
}
