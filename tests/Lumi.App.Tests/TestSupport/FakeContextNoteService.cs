using Lumi.Core.ContextNotes;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 研究本服務的假實作（M35）：記下每個呼叫（<see cref="Calls"/>，格式「方法名:參數」）、<see cref="GetViewAsync"/> 回 <see cref="Views"/> 裡設定的值、
/// 可以用 <see cref="RaiseNoteChanged"/>／<see cref="RaiseStatusChanged"/> 手動發事件。
/// </summary>
internal sealed class FakeContextNoteService : IContextNoteService
{
    private readonly object _lock = new();
    private readonly List<string> _calls = [];

    public event EventHandler<ContextNoteChangedArgs>? NoteChanged;
    public event EventHandler<ContextNoteStatus>? StatusChanged;

    /// <summary>對話 id → <see cref="GetViewAsync"/> 的回傳值。</summary>
    public Dictionary<string, ContextNoteView> Views { get; } = new(StringComparer.Ordinal);

    /// <summary>研究本 id → <see cref="GetStatus"/> 的回傳值；沒有設定回 Idle。</summary>
    public Dictionary<string, ContextNoteStatus> Statuses { get; } = new(StringComparer.Ordinal);

    /// <summary>不是 null 時，對應的方法丟這個例外（StartAsync、AddImageAsync、EndUserEditAsync、RevertAsync）。</summary>
    public Exception? ThrowOnStart { get; set; }
    public Exception? ThrowOnAddImage { get; set; }

    /// <summary><see cref="AddImageAsync"/> 回的相對路徑。</summary>
    public string NextImagePath { get; set; } = "images/20261116-120000-1.png";

    /// <summary><see cref="StartAsync"/> 建出來的研究本（測試可以讀）。</summary>
    public List<ContextNote> Started { get; } = [];

    public IReadOnlyList<string> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    /// <summary>最後一次 <see cref="EndUserEditAsync"/> 傳進來的內容（null＝沒有修改）。</summary>
    public string? LastEditContent { get; private set; }

    private void Record(string call)
    {
        lock (_lock)
            _calls.Add(call);
    }

    public void RaiseNoteChanged(ContextNoteChangedArgs args) => NoteChanged?.Invoke(this, args);
    public void RaiseStatusChanged(ContextNoteStatus status) => StatusChanged?.Invoke(this, status);

    public Task<ContextNoteView?> GetViewAsync(string conversationId, CancellationToken ct = default)
    {
        Record($"GetView:{conversationId}");
        return Task.FromResult(Views.GetValueOrDefault(conversationId));
    }

    public ContextNoteStatus GetStatus(string noteId) => Statuses.GetValueOrDefault(noteId) ?? new ContextNoteStatus(noteId, ScribeState.Idle);

    public Task<ContextNote> StartAsync(string conversationId, string goal, ContextNoteStartMode mode, CancellationToken ct = default)
    {
        Record($"Start:{conversationId}:{goal}:{mode}");
        if (ThrowOnStart is not null)
            throw ThrowOnStart;
        var now = DateTimeOffset.UnixEpoch;
        var note = new ContextNote(ContextNote.NewId(), conversationId, goal, true, 1, 0, null, null, now, now);
        var content = "## 問題拆解\n\n## 已決定\n\n## 待決問題\n\n## 觀點與選項\n\n## 否決的選項\n\n## 目前的解與待驗證\n\n## 旁支\n";
        var revision = new ContextNoteRevision(ContextNoteRevision.NewId(), note.Id, 1,
            mode == ContextNoteStartMode.Blank ? ContextNoteRevisionKind.StartBlank : ContextNoteRevisionKind.StartConversation,
            goal, content, [], null, 0, now);
        Views[conversationId] = new ContextNoteView(note, revision, null);
        Started.Add(note);
        return Task.FromResult(note);
    }

    public void NotifyTurnFinished(string conversationId) => Record($"NotifyTurnFinished:{conversationId}");

    public Task OrganizeNowAsync(string noteId, CancellationToken ct = default)
    {
        Record($"OrganizeNow:{noteId}");
        return Task.CompletedTask;
    }

    public void BeginUserEdit(string noteId) => Record($"BeginUserEdit:{noteId}");

    public Task EndUserEditAsync(string noteId, string? newContent, CancellationToken ct = default)
    {
        Record($"EndUserEdit:{noteId}:{(newContent is null ? "null" : "content")}");
        LastEditContent = newContent;
        return Task.CompletedTask;
    }

    public Task SetGoalAsync(string noteId, string goal, CancellationToken ct = default)
    {
        Record($"SetGoal:{noteId}:{goal}");
        return Task.CompletedTask;
    }

    public Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default)
    {
        Record($"SetAutoOrganize:{noteId}:{autoOrganize}");
        return Task.CompletedTask;
    }

    public Task MarkDecidedAsync(string noteId, string blockText, CancellationToken ct = default)
    {
        Record($"MarkDecided:{noteId}:{blockText}");
        return Task.CompletedTask;
    }

    public Task SetProtectedAsync(string noteId, string blockText, bool isProtected, CancellationToken ct = default)
    {
        Record($"SetProtected:{noteId}:{blockText}:{isProtected}");
        return Task.CompletedTask;
    }

    public Task RevertAsync(string noteId, int version, CancellationToken ct = default)
    {
        Record($"Revert:{noteId}:{version}");
        return Task.CompletedTask;
    }

    public Task DismissGoalHintAsync(string noteId, CancellationToken ct = default)
    {
        Record($"DismissGoalHint:{noteId}");
        return Task.CompletedTask;
    }

    public Task<string> AddImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
    {
        Record($"AddImage:{noteId}:{mediaType}");
        if (ThrowOnAddImage is not null)
            throw ThrowOnAddImage;
        return Task.FromResult(NextImagePath);
    }

    public Task SyncExternalAsync(string noteId, CancellationToken ct = default)
    {
        Record($"SyncExternal:{noteId}");
        return Task.CompletedTask;
    }
}
