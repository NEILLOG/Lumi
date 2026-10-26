namespace Lumi.Core.Chat;

/// <summary>某段期間內所有對話的 token 加總。<paramref name="Input"/> 含快取（輸入＋快取寫入＋快取讀取），<paramref name="CacheRead"/> 是其中命中快取的部分。</summary>
public sealed record TokenTotals(long Input, long CacheRead, long Output)
{
    public static readonly TokenTotals Zero = new(0, 0, 0);

    public long Total => Input + Output;
}

/// <summary>跨對話的「最近一次工具動作」，給設定首頁的「最近的動作」用。</summary>
public sealed record RecentToolAction(DateTimeOffset At, string ConversationId, ToolResultBlock Result);
