using Lumi.Core.Chat;

namespace Lumi.Core.Branches;

/// <summary>支線的狀態（<c>conversation_branches.status</c>：active／merged／abandoned）。</summary>
public enum BranchStatus { Active, Merged, Abandoned }

/// <summary><c>conversation_branches</c> 一列。<paramref name="ConversationId"/> 就是支線那則對話的 id。</summary>
/// <param name="RootId">最上層的主線；第一層支線的 RootId 等於 ParentId。</param>
/// <param name="Depth">第一層是 1，第二層是 2。</param>
/// <param name="ForkExcerpt">分岔那則回答的文字開頭（<see cref="BranchLimits.ForkExcerptChars"/> 字，空白縮成一個）。</param>
/// <param name="ForkMessageAt">分岔那則回答的建立時間。</param>
/// <param name="Summary">帶回時確認過的摘要；還沒帶回或放棄是 null。</param>
/// <param name="DeliveredMessageId">摘要放進上一層的哪一則使用者訊息；還沒交出是 null。</param>
public sealed record Branch(
    string ConversationId,
    string ParentId,
    string RootId,
    int Depth,
    string ForkMessageId,
    string ForkExcerpt,
    DateTimeOffset ForkMessageAt,
    string? Quote,
    BranchStatus Status,
    string? Summary,
    string? DeliveredMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EndedAt);

/// <summary>開不了支線的原因。</summary>
public enum BranchStartBlock { None, NotBranchable, Finished, Depth, ActiveChild, MessageNotFound }

/// <summary>路徑上的一節（主線是第一節，Depth 0、Status null）。</summary>
public sealed record BranchPathNode(string ConversationId, string Title, int Depth, BranchStatus? Status);

/// <summary>
/// 一則對話在支線結構裡的狀態：從主線到它自己的路徑、它自己的支線資料（是主線時 null）、
/// 它直接分出去的支線（依建立時間由舊到新），以及其中進行中的那一條（最多一條）。
/// </summary>
public sealed record BranchState(
    IReadOnlyList<BranchPathNode> Path,
    Branch? Current,
    IReadOnlyList<Branch> Children,
    Branch? ActiveChild)
{
    public BranchPathNode Root => Path[0];
    public bool IsBranch => Current is not null;
    public bool IsLocked => ActiveChild is not null;
    public bool IsFinished => Current is { Status: not BranchStatus.Active };
    public int Depth => Current?.Depth ?? 0;
}

/// <summary>整理摘要的結果：<paramref name="Text"/> 有值＝成功；否則看 <paramref name="Failure"/> 或 <paramref name="NoModel"/>。</summary>
public sealed record BranchSummaryDraft(string? Text, ChatFailure? Failure, bool NoModel = false);

/// <summary>交給 <see cref="IBranchSummarizer"/> 的輸入（4.5）。</summary>
/// <param name="RootId">用量記在哪一則對話。</param>
public sealed record BranchSummaryRequest(
    string RootId,
    string ForkText,
    string? Quote,
    string? MainLastUserText,
    IReadOnlyList<ChatMessage> BranchMessages,
    IReadOnlyList<(string Title, string Summary)> ChildSummaries);

public interface IBranchSummarizer
{
    /// <summary>失敗不丟例外，回失敗的結果；被取消照常丟。</summary>
    Task<BranchSummaryDraft> SummarizeAsync(BranchSummaryRequest request, CancellationToken ct = default);
}

public sealed record BranchChangedArgs(string ConversationId, string ParentId, string RootId, BranchStatus Status);

public static class BranchLimits
{
    public const int MaxDepth = 2;
    public const int QuoteMaxChars = 500;
    public const int QuoteDraftChars = 40;
    public const int ForkExcerptChars = 300;
    public const int OriginExcerptChars = 120;
    public const int TitleFromQuoteChars = 30;
    public const int SummaryMaxChars = 2_000;
    public const int SummarizerMaxOutputTokens = 1_500;
    public const int TranscriptMaxChars = 30_000;
    public const int MainContextForkChars = 1_500;
    public const int MainContextUserChars = 800;
    public const int ReadConversationSummaryChars = 300;
    public const string DefaultTitle = "支線";
}

/// <summary>給模型與錯誤列的固定文字（Core 不能用 App 的 Strings）。</summary>
public static class BranchMessages
{
    public const string ActiveChildMessage = "這則對話有進行中的支線，先帶回或放棄那條支線。";
    public const string FinishedBranchMessage = "這條支線已經結束，不能再送出。";
    public const string BranchTooLongMessage = "支線的內容太長了，超過模型能處理的長度。請帶回主線，或放棄這條支線。";
    public const string BranchCompactMessage = "支線不能整理上下文。內容太長時，請帶回主線或放棄這條支線。";
    public const string BranchEmptyMessage = "支線裡還沒有內容。";
    public const string SummaryEmptyMessage = "整理摘要時沒有取得內容。";
    public const string NotBranchableMessage = "這則對話不能開支線。";
    public const string FinishedParentMessage = "這條支線已經結束，不能再開支線。";
    public const string DepthMessage = "最多兩層支線，先帶回上一層再開。";
    public const string MessageNotFoundMessage = "找不到要分出去的那則回答。";
    public const string ReadBranchMessage = "這是支線，不能讀取。";
    public const string SuggestInBranchMessage = "支線裡不能開研究本。";

    public static string For(BranchStartBlock block) => block switch
    {
        BranchStartBlock.NotBranchable => NotBranchableMessage,
        BranchStartBlock.Finished => FinishedParentMessage,
        BranchStartBlock.Depth => DepthMessage,
        BranchStartBlock.ActiveChild => ActiveChildMessage,
        BranchStartBlock.MessageNotFound => MessageNotFoundMessage,
        _ => "",
    };
}
