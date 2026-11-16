namespace Lumi.Core.ContextNotes;

/// <summary>沒有研究本功能時的預設（<see cref="GetViewAsync"/> 回 null，其他不做事）。正式的 <c>ContextNoteService</c> 由整合時註冊。</summary>
public sealed class NullContextNoteService : IContextNoteService
{
    public event EventHandler<ContextNoteChangedArgs>? NoteChanged { add { } remove { } }
    public event EventHandler<ContextNoteStatus>? StatusChanged { add { } remove { } }

    public Task<ContextNoteView?> GetViewAsync(string conversationId, CancellationToken ct = default) => Task.FromResult<ContextNoteView?>(null);
    public ContextNoteStatus GetStatus(string noteId) => new(noteId, ScribeState.Idle);

    public Task<ContextNote> StartAsync(string conversationId, string goal, ContextNoteStartMode mode, CancellationToken ct = default) =>
        throw new NotSupportedException("研究本功能沒有啟用。");

    public void NotifyTurnFinished(string conversationId) { }
    public Task OrganizeNowAsync(string noteId, CancellationToken ct = default) => Task.CompletedTask;
    public void BeginUserEdit(string noteId) { }
    public Task EndUserEditAsync(string noteId, string? newContent, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetGoalAsync(string noteId, string goal, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default) => Task.CompletedTask;
    public Task MarkDecidedAsync(string noteId, string blockText, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetProtectedAsync(string noteId, string blockText, bool isProtected, CancellationToken ct = default) => Task.CompletedTask;
    public Task RevertAsync(string noteId, int version, CancellationToken ct = default) => Task.CompletedTask;
    public Task DismissGoalHintAsync(string noteId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> AddImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default) =>
        throw new NotSupportedException("研究本功能沒有啟用。");

    public Task SyncExternalAsync(string noteId, CancellationToken ct = default) => Task.CompletedTask;
}
