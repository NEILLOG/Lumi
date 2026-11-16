using Lumi.Core.Chat;

namespace Lumi.Core.ContextNotes;

/// <summary>一版是怎麼來的（<c>context_note_revisions.kind</c>，存小寫底線字串）。</summary>
public enum ContextNoteRevisionKind
{
    StartConversation, StartBlank, ScribeFirst, ScribeAuto, ScribeManual, UserEdit, UserAction, UserGoal, External, Revert,
}

/// <summary>研究本（M35）：一則對話一本（<c>context_notes</c> 一列＋一個資料夾）。畫面上叫「研究本」，程式裡叫 context note。</summary>
public sealed record ContextNote(
    string Id, string ConversationId, string Goal, bool AutoOrganize, int CurrentVersion,
    int ScribedUptoSeq, string? ScribedUptoMessageId, string? GoalHint,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}

/// <param name="Content">7 個區段的 Markdown（從「## 問題拆解」開始），不含檔案開頭的標題與目標。</param>
/// <param name="ProtectedBlocks">受保護區塊的完整文字。</param>
/// <param name="TriggerExcerpt">書記的版本：這批對話最後一則使用者訊息的前 30 字；退回：退回的目標版本號；其他是 null。</param>
public sealed record ContextNoteRevision(
    string Id, string NoteId, int Version, ContextNoteRevisionKind Kind, string Goal, string Content,
    IReadOnlyList<string> ProtectedBlocks, string? TriggerExcerpt, int RestoredCount, DateTimeOffset CreatedAt)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}

/// <summary>版本清單的一列（不含全文）。</summary>
public sealed record ContextNoteRevisionInfo(int Version, ContextNoteRevisionKind Kind, string? TriggerExcerpt, DateTimeOffset CreatedAt);

/// <summary>書記整理到哪裡。</summary>
public sealed record ContextNoteProgress(int UptoSeq, string? UptoMessageId);

public sealed record ContextNoteSuggestion(string ConversationId, DateTimeOffset SuggestedAt, DateTimeOffset? DismissedAt);

public enum ContextNoteStartMode { FromConversation, Blank }

public enum ScribeState { Idle, Running, WaitingForUser, Paused, Failed, Rejected, NoModel }

/// <param name="RestoredCount">最近一次整理放回了幾段受保護的內容（0＝沒有）。</param>
public sealed record ContextNoteStatus(string NoteId, ScribeState State, int RestoredCount = 0);

public sealed record ContextNoteChangedArgs(string ConversationId, string NoteId, int Version);

/// <summary>畫面要的一次讀取：研究本、目前版本、上一版（算「剛更新」用，沒有就是 null）。</summary>
public sealed record ContextNoteView(ContextNote Note, ContextNoteRevision Current, ContextNoteRevision? Previous);

/// <summary>書記一次呼叫的輸入。</summary>
public sealed record ScribeRequest(string ConversationId, string Goal, MaskResult Masked, TurnText Turn);

/// <param name="Output">解析過的回答；格式不對時是 null（<paramref name="Failure"/> 也是 null）。</param>
/// <param name="Failure">供應商失敗。</param>
/// <param name="NoModel">沒有可用的模型，沒有呼叫。</param>
public sealed record ScribeResult(ScribeOutput? Output, ChatFailure? Failure, bool NoModel = false);

/// <summary>整理研究本的那一次 AI 呼叫（畫面上叫「書記」）。實作 <c>ContextNoteScribe</c>（35E）。</summary>
public interface IContextNoteScribe
{
    Task<ScribeResult> RunAsync(ScribeRequest request, CancellationToken ct = default);
}

/// <summary>研究本的常數（計畫 4.13）。</summary>
public static class ContextNoteLimits
{
    /// <summary>目標字數上限。</summary>
    public const int GoalMaxLength = 200;
    /// <summary>目標提示上限。</summary>
    public const int GoalHintMaxLength = 200;
    /// <summary>研究本字數上限（書記與使用者都擋）。</summary>
    public const int NoteMaxChars = 20_000;
    /// <summary>超過就提醒可以關閉自動整理。</summary>
    public const int NoteLongChars = 16_000;
    /// <summary>書記輸出上限（再和模型的 MaxOutputTokens 取小）。</summary>
    public const int ScribeMaxOutputTokens = 8_000;
    /// <summary>每段對話文字超過這個長度就截斷：留前 <see cref="TurnTextHead"/> 與後 <see cref="TurnTextTail"/>。</summary>
    public const int TurnTextMaxChars = 6_000;
    public const int TurnTextHead = 4_000;
    public const int TurnTextTail = 2_000;
    /// <summary>工具結果只取前這麼多字。</summary>
    public const int TurnToolResultMaxChars = 600;
    /// <summary>一批最多幾則訊息。</summary>
    public const int TurnMaxMessages = 40;
    /// <summary>第一版讀整則對話的上限。</summary>
    public const int StartMaxChars = 60_000;
    /// <summary>改動幅度檢查：舊研究本沒受保護的區塊至少這麼多個才檢查。</summary>
    public const int ChangeGuardMinBlocks = 8;
    /// <summary>改動幅度檢查：超過這個比例找不到就擋。</summary>
    public const double ChangeGuardRatio = 0.5;
    /// <summary>每本研究本最多保留幾版（第 1 版永遠保留）。</summary>
    public const int MaxRevisions = 200;
    /// <summary>一次整理最多新增幾張圖。</summary>
    public const int MaxImagesPerRun = 4;
    /// <summary>壓縮後附整本研究本的上限。</summary>
    public const int AnchorFullNoteMaxChars = 12_000;
    /// <summary>版本原因（trigger_excerpt）的字數。</summary>
    public const int TriggerExcerptLength = 30;
    /// <summary>區塊超過這個長度就不做字的比對，整塊當成刪除＋新增。</summary>
    public const int InlineDiffMaxChars = 2_000;
}
