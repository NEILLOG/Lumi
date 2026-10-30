namespace Lumi.Core.Usage;

public enum PriceSource { None, Suggested, Confirmed }

/// <summary>模型設定上的單價（US$／每百萬 tokens）。只用來估算費用，改了只影響之後的請求。</summary>
/// <param name="CacheReadPerMTok">null 時以輸入價計算。</param>
/// <param name="CacheWritePerMTok">null 時以輸入價計算；Anthropic 取 5 分鐘的價。</param>
/// <param name="IsFree">本機模型；true 時費用一律 0。</param>
public sealed record ModelPricing(
    decimal? InputPerMTok,
    decimal? OutputPerMTok,
    decimal? CacheReadPerMTok = null,
    decimal? CacheWritePerMTok = null,
    bool IsFree = false,
    PriceSource Source = PriceSource.None)
{
    public static readonly ModelPricing None = new(null, null);

    /// <summary>算得出費用：免費，或輸入與輸出價都有。</summary>
    public bool IsSet => IsFree || (InputPerMTok is not null && OutputPerMTok is not null);
}

/// <summary>寫進 <c>request_usage.price_snapshot_json</c> 的單價：快取價已經解析（null 時用輸入價）。</summary>
public sealed record PriceSnapshot(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, bool IsFree)
{
    /// <summary>未設定單價回 null；免費回全 0 且 <see cref="IsFree"/>。</summary>
    public static PriceSnapshot? From(ModelPricing pricing)
    {
        if (pricing.IsFree)
            return new PriceSnapshot(0, 0, 0, 0, IsFree: true);
        if (pricing.InputPerMTok is not { } input || pricing.OutputPerMTok is not { } output)
            return null;
        return new PriceSnapshot(input, output, pricing.CacheReadPerMTok ?? input, pricing.CacheWritePerMTok ?? input, IsFree: false);
    }
}
