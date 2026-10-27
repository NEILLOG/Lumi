namespace Lumi.Core.Chat;

/// <summary>上下文壓縮的兩層（M22）。第一層不呼叫模型；第二層用模型把較早的內容整理成摘要。</summary>
public enum CompactionKind
{
    /// <summary>較早的工具結果換成一行「已省略」，圖片拿掉。</summary>
    ToolResults,

    /// <summary>較早的訊息整個換成一段 LLM 摘要。</summary>
    Summary,
}

/// <summary>
/// 一則對話目前的整理狀態（各取最新一筆）。整理只影響「送給模型的內容」：資料庫裡的原始訊息不變。
/// <paramref name="ToolResultsUptoSeq"/>：<c>seq</c> 小於等於它的工具結果送出時換成一行省略說明；
/// <paramref name="SummaryUptoSeq"/>：<c>seq</c> 小於等於它的訊息不再送出，改由 <paramref name="Summary"/> 取代；
/// <paramref name="AppliedAtSeq"/>：最近一次整理發生時，對話最後一則訊息的 <c>seq</c>
/// （在它之前回報的 token 用量已經不代表現在的請求大小，估算要略過）。
/// </summary>
public sealed record CompactionState(int? ToolResultsUptoSeq, int? SummaryUptoSeq, string? Summary, int AppliedAtSeq = 0)
{
    public static readonly CompactionState Empty = new(null, null, null);

    /// <summary>還沒有任何整理。</summary>
    public bool IsEmpty => ToolResultsUptoSeq is null && SummaryUptoSeq is null;
}

/// <summary>
/// 要存檔的一筆整理紀錄。<paramref name="Usage"/> 是摘要那次呼叫的 token 用量（第一層為 null）：
/// 用的是同一把金鑰的錢，所以計入本對話與本月用量。
/// </summary>
public sealed record CompactionRecord(
    string Id,
    CompactionKind Kind,
    int UptoSeq,
    string? Summary,
    string? ModelId,
    DateTimeOffset CreatedAt,
    TokenUsage? Usage = null)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}

/// <summary>
/// 手動整理（<see cref="IChatSession.CompactAsync"/>）的結果。<paramref name="Compacted"/> 是 false 且
/// <paramref name="Failure"/> 是 null 表示沒有東西可整理（對話還太短，或已經整理到最新）。
/// </summary>
public sealed record CompactionResult(bool Compacted, ChatFailure? Failure = null);
