using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>
/// 本對話的 token 累計（ui-usage-and-topbar.md 第 4 節）。純類別，不依賴 UI，方便測試。
/// <list type="bullet">
/// <item><see cref="Committed"/>：已存進資料庫的助理訊息 usage 加總（載入時加總，完成時併入）。</item>
/// <item><see cref="InFlight"/>：進行中那一次請求最新的 <see cref="UsageEvent"/>。它本身就是該次請求的累計值，所以只保留最後一個、不相加。</item>
/// </list>
/// 對外的累計是兩者相加。快取欄位統一為：輸入含快取＝<c>InputTokens + CacheCreationInputTokens + CacheReadInputTokens</c>
/// （三家供應商的 <see cref="TokenUsage"/> 已在 Providers 轉成同一種意義）。
/// </summary>
public sealed class ConversationUsageTally
{
    private bool _hasCommitted;

    /// <summary>已存進資料庫的訊息 usage 加總。</summary>
    public TokenUsage Committed { get; private set; } = TokenUsage.Zero;

    /// <summary>進行中請求的最新累計值；沒有進行中的請求時是 null。</summary>
    public TokenUsage? InFlight { get; private set; }

    /// <summary>顯示用的累計：已存＋進行中。</summary>
    public TokenUsage Total => InFlight is null ? Committed : Add(Committed, InFlight);

    /// <summary>有任何 usage（已存或進行中）。</summary>
    public bool HasData => _hasCommitted || InFlight is not null;

    /// <summary>累計輸入（含快取讀取與寫入）。</summary>
    public int TotalInputIncludingCache => InputIncludingCache(Total);

    /// <summary>累計快取讀取。</summary>
    public int TotalCacheRead => Total.CacheReadInputTokens;

    /// <summary>累計輸出。</summary>
    public int TotalOutput => Total.OutputTokens;

    /// <summary>整組換掉：切換對話或載入歷史時用，依序加總各則助理訊息的 usage（null 略過）。進行中的請求一併清掉。</summary>
    public void Load(IEnumerable<TokenUsage?> storedUsages)
    {
        var sum = TokenUsage.Zero;
        var any = false;
        foreach (var usage in storedUsages)
        {
            if (usage is null)
                continue;
            sum = Add(sum, usage);
            any = true;
        }
        Committed = sum;
        _hasCommitted = any;
        InFlight = null;
    }

    /// <summary>新對話或切換後歸零（等同 <see cref="Load"/> 空的集合）。</summary>
    public void Reset() => Load([]);

    /// <summary>收到 <see cref="UsageEvent"/>：這是進行中請求的最新累計值，覆蓋而不是相加。</summary>
    public void ReportInFlight(TokenUsage usage) => InFlight = usage;

    /// <summary>
    /// 一次請求完成（<see cref="Completed"/>）：把該訊息的 usage 併入已存，清掉進行中。
    /// 訊息沒有 usage 時退回用進行中的最新值。
    /// </summary>
    public void Complete(TokenUsage? messageUsage)
    {
        var final = messageUsage ?? InFlight;
        if (final is not null)
        {
            Committed = Add(Committed, final);
            _hasCommitted = true;
        }
        InFlight = null;
    }

    /// <summary>請求失敗或中止：沒有定稿的那次不計入（以資料庫實際存下來的為準）。</summary>
    public void Abandon() => InFlight = null;

    public static int InputIncludingCache(TokenUsage usage) =>
        usage.InputTokens + usage.CacheCreationInputTokens + usage.CacheReadInputTokens;

    private static TokenUsage Add(TokenUsage a, TokenUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheCreationInputTokens + b.CacheCreationInputTokens,
        a.CacheReadInputTokens + b.CacheReadInputTokens);
}
