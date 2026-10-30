namespace Lumi.Core.Usage;

/// <summary>價目表的來源：目前使用的版本，以及從網路更新（失敗就沿用目前的版本）。實作在 Lumi.Web。</summary>
public interface IPriceTableSource
{
    PriceTable Current { get; }

    Task<PriceTableUpdateResult> UpdateAsync(CancellationToken ct);
}

/// <param name="Success">true＝已經換成新版本；false＝沒有覆蓋（網路失敗、內容讀不懂或筆數不足）。</param>
/// <param name="SnapshotDate">成功時是新版本的日期；失敗時是目前版本的日期。</param>
public sealed record PriceTableUpdateResult(bool Success, DateOnly SnapshotDate);
