using System.Globalization;
using System.Text.Json;
using Lumi.Core.ContextNotes;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.ContextNotes;

/// <summary>
/// <see cref="IContextNoteStore"/> 的 SQLite 實作（資料表 <c>context_notes</c>、<c>context_note_revisions</c>、<c>context_note_suggestions</c>，migration 14）。
/// 和 <see cref="SqliteConversationStore"/> 共用同一個資料庫與初始化；刪除對話時靠外鍵 ON DELETE CASCADE 一併刪掉。
/// kind 存小寫底線字串，<c>protected_json</c> 存字串陣列，<c>auto_organize</c> 存 0／1，時間存 UTC 固定格式。
/// </summary>
public sealed class SqliteContextNoteStore(SqliteConversationStore database) : IContextNoteStore
{
    private const string NoteColumns =
        "id, conversation_id, goal, auto_organize, current_version, scribed_upto_seq, scribed_upto_message_id, goal_hint, created_at, updated_at";

    private const string RevisionColumns =
        "id, note_id, version, kind, goal, content, protected_json, trigger_excerpt, restored_count, created_at";

    // SQLite 的 UNIQUE／PRIMARY KEY 違反（研究本一則對話只能一本）
    private const int SqliteUniqueConstraint = 2067;
    private const int SqlitePrimaryKeyConstraint = 1555;

    public async Task<ContextNote?> GetByConversationAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {NoteColumns} FROM context_notes WHERE conversation_id = $conversation LIMIT 1;";
        command.Parameters.AddWithValue("$conversation", conversationId);
        return (await ReadNotesAsync(command, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<ContextNote?> GetAsync(string noteId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {NoteColumns} FROM context_notes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", noteId);
        return (await ReadNotesAsync(command, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<ContextNoteRevision?> GetRevisionAsync(string noteId, int version, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RevisionColumns} FROM context_note_revisions WHERE note_id = $note AND version = $version;";
        command.Parameters.AddWithValue("$note", noteId);
        command.Parameters.AddWithValue("$version", version);
        return (await ReadRevisionsAsync(command, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ContextNoteRevisionInfo>> ListRevisionsAsync(string noteId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 只讀列表需要的欄位，不讀全文
        command.CommandText = """
            SELECT version, kind, trigger_excerpt, created_at FROM context_note_revisions
            WHERE note_id = $note ORDER BY version DESC;
            """;
        command.Parameters.AddWithValue("$note", noteId);
        var result = new List<ContextNoteRevisionInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ContextNoteRevisionInfo(
                Version: reader.GetInt32(0),
                Kind: ParseKind(reader.GetString(1)),
                TriggerExcerpt: reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt: ParseTime(reader.GetString(3))));
        }
        return result;
    }

    public async Task CreateAsync(ContextNote note, ContextNoteRevision first, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(first);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    INSERT INTO context_notes ({NoteColumns})
                    VALUES ($id, $conversation, $goal, $auto, $version, $seq, $message, $hint, $created, $updated);
                    """;
                command.Parameters.AddWithValue("$id", note.Id);
                command.Parameters.AddWithValue("$conversation", note.ConversationId);
                command.Parameters.AddWithValue("$goal", first.Goal);
                command.Parameters.AddWithValue("$auto", note.AutoOrganize ? 1 : 0);
                command.Parameters.AddWithValue("$version", first.Version);
                command.Parameters.AddWithValue("$seq", note.ScribedUptoSeq);
                command.Parameters.AddWithValue("$message", (object?)note.ScribedUptoMessageId ?? DBNull.Value);
                command.Parameters.AddWithValue("$hint", (object?)note.GoalHint ?? DBNull.Value);
                command.Parameters.AddWithValue("$created", FormatTime(note.CreatedAt));
                command.Parameters.AddWithValue("$updated", FormatTime(note.UpdatedAt));
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await InsertRevisionAsync(connection, transaction, first, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode is SqliteUniqueConstraint or SqlitePrimaryKeyConstraint)
        {
            // 這則對話已經有研究本（conversation_id UNIQUE）
            throw new InvalidOperationException("這則對話已經有研究本。", ex);
        }
    }

    public async Task AddRevisionAsync(ContextNoteRevision revision, ContextNoteProgress? progress = null, bool setGoalHint = false, string? goalHint = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revision);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await InsertRevisionAsync(connection, transaction, revision, ct).ConfigureAwait(false);

        // 研究本指向這一版；進度與目標提示只在呼叫端要求時才改
        var sets = new List<string> { "current_version = $version", "goal = $goal", "updated_at = $updated" };
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.Parameters.AddWithValue("$version", revision.Version);
            update.Parameters.AddWithValue("$goal", revision.Goal);
            update.Parameters.AddWithValue("$updated", FormatTime(revision.CreatedAt));
            update.Parameters.AddWithValue("$id", revision.NoteId);
            if (progress is not null)
            {
                sets.Add("scribed_upto_seq = $seq");
                sets.Add("scribed_upto_message_id = $message");
                update.Parameters.AddWithValue("$seq", progress.UptoSeq);
                update.Parameters.AddWithValue("$message", (object?)progress.UptoMessageId ?? DBNull.Value);
            }
            if (setGoalHint)
            {
                sets.Add("goal_hint = $hint");
                update.Parameters.AddWithValue("$hint", (object?)goalHint ?? DBNull.Value);
            }
            update.CommandText = $"UPDATE context_notes SET {string.Join(", ", sets)} WHERE id = $id;";
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // 修剪：超過上限時刪掉最舊的，第 1 版永遠保留
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM context_note_revisions WHERE note_id = $note;";
            count.Parameters.AddWithValue("$note", revision.NoteId);
            var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var excess = total - ContextNoteLimits.MaxRevisions;
            if (excess > 0)
            {
                await using var prune = connection.CreateCommand();
                prune.Transaction = transaction;
                prune.CommandText = """
                    DELETE FROM context_note_revisions WHERE note_id = $note AND version IN (
                      SELECT version FROM context_note_revisions WHERE note_id = $note AND version > 1 ORDER BY version ASC LIMIT $excess
                    );
                    """;
                prune.Parameters.AddWithValue("$note", revision.NoteId);
                prune.Parameters.AddWithValue("$excess", excess);
                await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task SetProgressAsync(string noteId, ContextNoteProgress progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return UpdateNoteAsync(noteId, "scribed_upto_seq = $seq, scribed_upto_message_id = $message", ct, ("$seq", progress.UptoSeq), ("$message", progress.UptoMessageId));
    }

    public Task SetAutoOrganizeAsync(string noteId, bool autoOrganize, CancellationToken ct = default) =>
        UpdateNoteAsync(noteId, "auto_organize = $auto", ct, ("$auto", autoOrganize ? 1 : 0));

    public Task SetGoalHintAsync(string noteId, string? goalHint, CancellationToken ct = default) =>
        UpdateNoteAsync(noteId, "goal_hint = $hint", ct, ("$hint", goalHint));

    public async Task<ContextNoteSuggestion?> GetSuggestionAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_id, suggested_at, dismissed_at FROM context_note_suggestions WHERE conversation_id = $conversation;";
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new ContextNoteSuggestion(
            ConversationId: reader.GetString(0),
            SuggestedAt: ParseTime(reader.GetString(1)),
            DismissedAt: reader.IsDBNull(2) ? null : ParseTime(reader.GetString(2)));
    }

    public async Task<bool> TryMarkSuggestedAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 已經有紀錄就忽略（第一次建議的時間保留）
        command.CommandText = "INSERT OR IGNORE INTO context_note_suggestions (conversation_id, suggested_at, dismissed_at) VALUES ($conversation, $at, NULL);";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$at", FormatTime(at));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task DismissSuggestionAsync(string conversationId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 沒有紀錄時先建立（suggested_at 用這次的時間），有紀錄就只設 dismissed_at
        command.CommandText = """
            INSERT INTO context_note_suggestions (conversation_id, suggested_at, dismissed_at) VALUES ($conversation, $at, $at)
            ON CONFLICT (conversation_id) DO UPDATE SET dismissed_at = $at;
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$at", FormatTime(at));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>更新研究本的一或多個欄位；沒有這本研究本時丟 <see cref="InvalidOperationException"/>（同記憶體版）。</summary>
    private async Task UpdateNoteAsync(string noteId, string assignments, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE context_notes SET {assignments} WHERE id = $id;";
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", noteId);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
            throw new InvalidOperationException("找不到研究本。");
    }

    private static async Task InsertRevisionAsync(SqliteConnection connection, SqliteTransaction transaction, ContextNoteRevision revision, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO context_note_revisions ({RevisionColumns})
            VALUES ($id, $note, $version, $kind, $goal, $content, $protected, $trigger, $restored, $created);
            """;
        command.Parameters.AddWithValue("$id", revision.Id);
        command.Parameters.AddWithValue("$note", revision.NoteId);
        command.Parameters.AddWithValue("$version", revision.Version);
        command.Parameters.AddWithValue("$kind", FormatKind(revision.Kind));
        command.Parameters.AddWithValue("$goal", revision.Goal);
        command.Parameters.AddWithValue("$content", revision.Content);
        command.Parameters.AddWithValue("$protected", JsonSerializer.Serialize(revision.ProtectedBlocks));
        command.Parameters.AddWithValue("$trigger", (object?)revision.TriggerExcerpt ?? DBNull.Value);
        command.Parameters.AddWithValue("$restored", revision.RestoredCount);
        command.Parameters.AddWithValue("$created", FormatTime(revision.CreatedAt));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<ContextNote>> ReadNotesAsync(SqliteCommand command, CancellationToken ct)
    {
        var notes = new List<ContextNote>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            notes.Add(new ContextNote(
                Id: reader.GetString(0),
                ConversationId: reader.GetString(1),
                Goal: reader.GetString(2),
                AutoOrganize: reader.GetInt32(3) != 0,
                CurrentVersion: reader.GetInt32(4),
                ScribedUptoSeq: reader.GetInt32(5),
                ScribedUptoMessageId: reader.IsDBNull(6) ? null : reader.GetString(6),
                GoalHint: reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt: ParseTime(reader.GetString(8)),
                UpdatedAt: ParseTime(reader.GetString(9))));
        }
        return notes;
    }

    private static async Task<List<ContextNoteRevision>> ReadRevisionsAsync(SqliteCommand command, CancellationToken ct)
    {
        var revisions = new List<ContextNoteRevision>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            revisions.Add(new ContextNoteRevision(
                Id: reader.GetString(0),
                NoteId: reader.GetString(1),
                Version: reader.GetInt32(2),
                Kind: ParseKind(reader.GetString(3)),
                Goal: reader.GetString(4),
                Content: reader.GetString(5),
                ProtectedBlocks: JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [],
                TriggerExcerpt: reader.IsDBNull(7) ? null : reader.GetString(7),
                RestoredCount: reader.GetInt32(8),
                CreatedAt: ParseTime(reader.GetString(9))));
        }
        return revisions;
    }

    private static string FormatKind(ContextNoteRevisionKind kind) => kind switch
    {
        ContextNoteRevisionKind.StartConversation => "start_conversation",
        ContextNoteRevisionKind.StartBlank => "start_blank",
        ContextNoteRevisionKind.ScribeFirst => "scribe_first",
        ContextNoteRevisionKind.ScribeAuto => "scribe_auto",
        ContextNoteRevisionKind.ScribeManual => "scribe_manual",
        ContextNoteRevisionKind.UserEdit => "user_edit",
        ContextNoteRevisionKind.UserAction => "user_action",
        ContextNoteRevisionKind.UserGoal => "user_goal",
        ContextNoteRevisionKind.External => "external",
        ContextNoteRevisionKind.Revert => "revert",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ContextNoteRevisionKind ParseKind(string text) => text switch
    {
        "start_conversation" => ContextNoteRevisionKind.StartConversation,
        "start_blank" => ContextNoteRevisionKind.StartBlank,
        "scribe_first" => ContextNoteRevisionKind.ScribeFirst,
        "scribe_auto" => ContextNoteRevisionKind.ScribeAuto,
        "scribe_manual" => ContextNoteRevisionKind.ScribeManual,
        "user_edit" => ContextNoteRevisionKind.UserEdit,
        "user_action" => ContextNoteRevisionKind.UserAction,
        "user_goal" => ContextNoteRevisionKind.UserGoal,
        "external" => ContextNoteRevisionKind.External,
        "revert" => ContextNoteRevisionKind.Revert,
        _ => throw new InvalidOperationException($"未知的研究本版本種類：{text}"),
    };

    // 一律存 UTC、固定格式（與 SqliteScheduledRunStore 相同）
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
