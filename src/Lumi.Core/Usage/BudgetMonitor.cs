using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.Core.Usage;

/// <summary>預算提醒的門檻（值就是百分比）。</summary>
public enum BudgetAlertLevel { Eighty = 80, Hundred = 100 }

/// <summary>
/// 每月預算的提醒判斷（plan/23-usage-cost.md 第 5 節）。只提醒，不停止回應。
/// 每個月每個門檻最多提醒一次，記在 <c>app_meta</c>（<see cref="BudgetAlertedKey"/>，值 <c>yyyy-MM:80</c>／<c>yyyy-MM:100</c>）。
/// </summary>
public sealed class BudgetMonitor(IUsageStore usage, IConversationStore meta, IModelCatalog catalog, TimeProvider time)
{
    /// <summary>應用程式設定表（<c>app_meta</c>）裡記提醒狀態的 key。</summary>
    public const string BudgetAlertedKey = "budget_alerted";

    // 同時有兩個回應結束時，避免同一個門檻提醒兩次。
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 回應結束後呼叫。本月花費跨過 80% 或 100%，而且這個門檻本月還沒提醒過，就回傳該門檻並寫下紀錄；否則回 null。
    /// 兩個門檻一起跨過時只回 100%。沒設預算（或 ≤ 0）時不檢查。
    /// </summary>
    public async Task<BudgetAlertLevel?> CheckAsync(CancellationToken ct = default)
    {
        var budget = Budget();
        if (budget is not { } limit)
            return null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            var zone = time.LocalTimeZone;
            var (from, to) = MonthlyUsageSummary.MonthRange(now, zone);
            var spent = await usage.SumCostAsync(from, to, ct).ConfigureAwait(false);

            var reached = Reached(spent, limit);
            if (reached is not { } level)
                return null;

            var month = MonthKey(now, zone);
            var alerted = await AlertedLevelAsync(month, ct).ConfigureAwait(false);
            if ((int)level <= alerted)
                return null;

            await meta.SetMetaAsync(BudgetAlertedKey, month + ":" + ((int)level).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            return level;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>本月已花費（美元）與每月預算（沒設回 null），給用量面板與提醒文字用。</summary>
    public async Task<(decimal SpentUsd, decimal? BudgetUsd)> GetMonthAsync(CancellationToken ct = default)
    {
        var (from, to) = MonthlyUsageSummary.MonthRange(time.GetUtcNow(), time.LocalTimeZone);
        var spent = await usage.SumCostAsync(from, to, ct).ConfigureAwait(false);
        return (spent, Budget());
    }

    private decimal? Budget()
    {
        var budget = catalog.Current.Usage.MonthlyBudgetUsd;
        return budget is decimal b && b > 0 ? b : null;
    }

    private static BudgetAlertLevel? Reached(decimal spent, decimal budget)
    {
        if (spent >= budget)
            return BudgetAlertLevel.Hundred;
        if (spent >= budget * 0.8m)
            return BudgetAlertLevel.Eighty;
        return null;
    }

    /// <summary>本月已提醒的最高門檻；不是本月的紀錄、或沒有紀錄都當作 0。</summary>
    private async Task<int> AlertedLevelAsync(string month, CancellationToken ct)
    {
        var stored = await meta.GetMetaAsync(BudgetAlertedKey, ct).ConfigureAwait(false);
        if (stored is null)
            return 0;

        var separator = stored.LastIndexOf(':');
        if (separator < 0 || stored[..separator] != month)
            return 0;

        return int.TryParse(stored[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? level : 0;
    }

    private static string MonthKey(DateTimeOffset now, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
