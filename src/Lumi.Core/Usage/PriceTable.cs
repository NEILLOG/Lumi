namespace Lumi.Core.Usage;

/// <summary>價目表的一筆（LiteLLM 條目換算成每百萬 tokens 美元）。</summary>
/// <param name="Key">LiteLLM 的鍵名，例如 <c>claude-sonnet-4-5</c> 或 <c>azure/gpt-4o</c>。</param>
/// <param name="Provider"><c>litellm_provider</c>；沒有時是 null。</param>
public sealed record PriceEntry(
    string Key,
    string? Provider,
    decimal InputPerMTok,
    decimal OutputPerMTok,
    decimal? CacheReadPerMTok,
    decimal? CacheWritePerMTok,
    int? MaxInputTokens,
    int? MaxOutputTokens);

/// <summary>一份價目表（內建快照或使用者資料夾的更新版）。不可變。</summary>
/// <param name="snapshotDate">這份資料的快照日期（設定頁顯示「價目表日期」用）。</param>
public sealed class PriceTable(IReadOnlyList<PriceEntry> entries, DateOnly snapshotDate)
{
    public static readonly PriceTable Empty = new([], DateOnly.MinValue);

    public IReadOnlyList<PriceEntry> Entries { get; } = entries;

    public DateOnly SnapshotDate { get; } = snapshotDate;
}
