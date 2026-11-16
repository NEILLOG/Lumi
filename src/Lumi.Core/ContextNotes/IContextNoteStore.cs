namespace Lumi.Core.ContextNotes;

/// <summary>研究本、版本、建議紀錄的儲存。實作 <c>SqliteContextNoteStore</c>（Lumi.Infrastructure）；Core 有記憶體版 <see cref="InMemoryContextNoteStore"/>。</summary>
public interface IContextNoteStore
{
    Task<ContextNote?> GetByConversationAsync(string conversationId, CancellationToken ct = default);
    Task<ContextNote?> GetAsync(string noteId, CancellationToken ct = default);
    Task<ContextNoteRevision?> GetRevisionAsync(string noteId, int version, CancellationToken ct = default);
    /// <summary>由新到舊。</summary>
    Task<IReadOnlyList<ContextNoteRevisionInfo>> ListRevisionsAsync(string noteId, CancellationToken ct = default);
    /// <summary>研究本與第一版在同一個交易裡建立。這則對話已經有研究本時丟 <see cref="InvalidOperationException"/>。</summary>
    Task CreateAsync(ContextNote note, ContextNoteRevision first, CancellationToken ct = default);
    /// <summary>
    /// 同一個交易：新增一版、把研究本的 current_version、goal、updated_at 改成這一版的；<paramref name="progress"/> 有值時一併更新進度；
    /// <paramref name="setGoalHint"/> 是 true 時把 goal_hint 改成 <paramref name="goalHint"/>。
    /// 超過 <see cref="ContextNoteLimits.MaxRevisions"/> 時刪最舊的（第 1 版除外）。
    /// </summary>
    Task AddRevisionAsync(ContextNoteRevision revision, ContextNoteProgress? progress = null, bool setGoalHint = false, string? goalHint = null, CancellationToken ct = default);
    Task SetProgressAsync(string noteId, ContextNoteProgress progress, CancellationToken ct = default);
    Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default);
    Task SetGoalHintAsync(string noteId, string? goalHint, CancellationToken ct = default);
    Task<ContextNoteSuggestion?> GetSuggestionAsync(string conversationId, CancellationToken ct = default);
    /// <returns>這則對話第一次建議回 true；已經有紀錄回 false（不改）。</returns>
    Task<bool> TryMarkSuggestedAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default);
    /// <summary>沒有紀錄時先建立再標成略過。</summary>
    Task DismissSuggestionAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>成品資料夾的檔案操作。實作在 Lumi.Infrastructure；Core 有記憶體版（測試與預設）。</summary>
public interface IContextNoteFiles
{
    string GetFolder(string noteId);
    Task<string?> ReadNoteAsync(string noteId, CancellationToken ct = default);
    /// <summary>寫暫存檔再取代；資料夾不存在就建立。</summary>
    Task WriteNoteAsync(string noteId, string fileText, CancellationToken ct = default);
    /// <returns>相對路徑，例如 <c>images/20261116-231205-1.png</c>。格式不支援或太大時丟 <see cref="ArgumentException"/>。</returns>
    Task<string> SaveImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default);
    /// <summary>只接受 <c>images/檔名</c>；有 <c>..</c>、絕對路徑、檔案不存在都回 null。</summary>
    string? ResolveImagePath(string noteId, string relativePath);
    Task DeleteAsync(string noteId, CancellationToken ct = default);
    Task ExportZipAsync(string noteId, string destinationZipPath, CancellationToken ct = default);
}

/// <summary>研究本的所有操作。事件可能從背景執行緒發出，畫面要自己切回 UI 執行緒。方法都不丟例外給書記的失敗（改用狀態）。</summary>
public interface IContextNoteService
{
    event EventHandler<ContextNoteChangedArgs>? NoteChanged;
    event EventHandler<ContextNoteStatus>? StatusChanged;

    Task<ContextNoteView?> GetViewAsync(string conversationId, CancellationToken ct = default);
    ContextNoteStatus GetStatus(string noteId);

    /// <summary>建立研究本與第一版。<see cref="ContextNoteStartMode.FromConversation"/> 時第一版先是空的骨架，再在背景整理整則對話。</summary>
    Task<ContextNote> StartAsync(string conversationId, string goal, ContextNoteStartMode mode, CancellationToken ct = default);

    /// <summary>一輪回應成功結束。沒有研究本、自動整理關閉時不做事。不等待。</summary>
    void NotifyTurnFinished(string conversationId);
    /// <summary>手動整理（不看自動整理的開關）。等到這次整理結束才回來。</summary>
    Task OrganizeNowAsync(string noteId, CancellationToken ct = default);

    void BeginUserEdit(string noteId);
    /// <param name="newContent">null 或和目前版本相同＝沒有修改。</param>
    Task EndUserEditAsync(string noteId, string? newContent, CancellationToken ct = default);

    Task SetGoalAsync(string noteId, string goal, CancellationToken ct = default);
    Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default);
    Task MarkDecidedAsync(string noteId, string blockText, CancellationToken ct = default);
    Task SetProtectedAsync(string noteId, string blockText, bool isProtected, CancellationToken ct = default);
    Task RevertAsync(string noteId, int version, CancellationToken ct = default);
    Task DismissGoalHintAsync(string noteId, CancellationToken ct = default);
    Task<string> AddImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default);
    /// <summary>比對 note.md 與目前版本，不同就存一版「外部修改」。</summary>
    Task SyncExternalAsync(string noteId, CancellationToken ct = default);
}
