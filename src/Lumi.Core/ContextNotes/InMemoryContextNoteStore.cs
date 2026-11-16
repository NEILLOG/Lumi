namespace Lumi.Core.ContextNotes;

/// <summary>
/// <see cref="IContextNoteStore"/> 的記憶體版本，給測試與預設用。行為與 <c>SqliteContextNoteStore</c> 一致（版本修剪、一則對話一本）。
/// 執行緒安全（一把鎖）。沒有對話表，刪除對話時的外鍵串連刪除不在這裡模擬；測試需要時用 <see cref="RemoveConversation"/>。
/// </summary>
public sealed class InMemoryContextNoteStore : IContextNoteStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ContextNote> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<int, ContextNoteRevision>> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContextNoteSuggestion> _suggestions = new(StringComparer.Ordinal);

    public Task<ContextNote?> GetByConversationAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_notes.Values.FirstOrDefault(n => n.ConversationId == conversationId));
    }

    public Task<ContextNote?> GetAsync(string noteId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_notes.GetValueOrDefault(noteId));
    }

    public Task<ContextNoteRevision?> GetRevisionAsync(string noteId, int version, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_revisions.TryGetValue(noteId, out var list) ? list.GetValueOrDefault(version) : null);
    }

    public Task<IReadOnlyList<ContextNoteRevisionInfo>> ListRevisionsAsync(string noteId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ContextNoteRevisionInfo> result = _revisions.TryGetValue(noteId, out var list)
                ? list.Values.Reverse().Select(r => new ContextNoteRevisionInfo(r.Version, r.Kind, r.TriggerExcerpt, r.CreatedAt)).ToArray()
                : [];
            return Task.FromResult(result);
        }
    }

    public Task CreateAsync(ContextNote note, ContextNoteRevision first, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(first);
        lock (_lock)
        {
            if (_notes.ContainsKey(note.Id) || _notes.Values.Any(n => n.ConversationId == note.ConversationId))
                throw new InvalidOperationException("這則對話已經有研究本。");
            _notes[note.Id] = note with { CurrentVersion = first.Version, Goal = first.Goal };
            _revisions[note.Id] = new SortedDictionary<int, ContextNoteRevision> { [first.Version] = Copy(first) };
        }
        return Task.CompletedTask;
    }

    public Task AddRevisionAsync(ContextNoteRevision revision, ContextNoteProgress? progress = null, bool setGoalHint = false, string? goalHint = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        lock (_lock)
        {
            var note = Require(revision.NoteId);
            var list = _revisions[revision.NoteId];
            if (list.ContainsKey(revision.Version))
                throw new InvalidOperationException($"第 {revision.Version} 版已經存在。");
            list[revision.Version] = Copy(revision);
            note = note with { CurrentVersion = revision.Version, Goal = revision.Goal, UpdatedAt = revision.CreatedAt };
            if (progress is not null)
                note = note with { ScribedUptoSeq = progress.UptoSeq, ScribedUptoMessageId = progress.UptoMessageId };
            if (setGoalHint)
                note = note with { GoalHint = goalHint };
            _notes[note.Id] = note;
            while (list.Count > ContextNoteLimits.MaxRevisions)
            {
                var oldest = list.Keys.FirstOrDefault(v => v > 1, -1);
                if (oldest < 0)
                    break;
                list.Remove(oldest);
            }
        }
        return Task.CompletedTask;
    }

    public Task SetProgressAsync(string noteId, ContextNoteProgress progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        lock (_lock)
            _notes[noteId] = Require(noteId) with { ScribedUptoSeq = progress.UptoSeq, ScribedUptoMessageId = progress.UptoMessageId };
        return Task.CompletedTask;
    }

    public Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default)
    {
        lock (_lock)
            _notes[noteId] = Require(noteId) with { AutoOrganize = autoOrganize };
        return Task.CompletedTask;
    }

    public Task SetGoalHintAsync(string noteId, string? goalHint, CancellationToken ct = default)
    {
        lock (_lock)
            _notes[noteId] = Require(noteId) with { GoalHint = goalHint };
        return Task.CompletedTask;
    }

    public Task<ContextNoteSuggestion?> GetSuggestionAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_suggestions.GetValueOrDefault(conversationId));
    }

    public Task<bool> TryMarkSuggestedAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_suggestions.TryAdd(conversationId, new ContextNoteSuggestion(conversationId, at, null)));
    }

    public Task DismissSuggestionAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var existing = _suggestions.GetValueOrDefault(conversationId) ?? new ContextNoteSuggestion(conversationId, at, null);
            _suggestions[conversationId] = existing with { DismissedAt = at };
        }
        return Task.CompletedTask;
    }

    /// <summary>模擬刪除對話時資料庫的串連刪除（研究本、版本、建議紀錄）。</summary>
    public void RemoveConversation(string conversationId)
    {
        lock (_lock)
        {
            foreach (var note in _notes.Values.Where(n => n.ConversationId == conversationId).ToArray())
            {
                _notes.Remove(note.Id);
                _revisions.Remove(note.Id);
            }
            _suggestions.Remove(conversationId);
        }
    }

    private ContextNote Require(string noteId) =>
        _notes.GetValueOrDefault(noteId) ?? throw new InvalidOperationException("找不到研究本。");

    private static ContextNoteRevision Copy(ContextNoteRevision revision) =>
        revision with { ProtectedBlocks = revision.ProtectedBlocks.ToArray() };
}
