using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IScheduledRunStore"/> 的 SQLite 實作（資料表 <c>scheduled_runs</c>，migration 13）。
/// 和 <see cref="SqliteConversationStore"/> 共用同一個資料庫與初始化（連線與 schema 由它負責）。
/// status／trigger 存小寫底線字串，時間存 UTC 固定格式。
/// </summary>
public sealed class SqliteScheduledRunStore(SqliteConversationStore database) : IScheduledRunStore
{
    private const string Columns =
        "id, schedule_id, schedule_title, conversation_id, planned_at, started_at, finished_at, trigger, status, failure_text, read_at";

    // 執行中與待確認（<see cref="ScheduledRun.IsActive"/>）
    private const string ActiveStatuses = "'running', 'awaiting_approval'";

    public async Task AddAsync(ScheduledRun run, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO scheduled_runs ({Columns})
            VALUES ($id, $schedule, $title, $conversation, $planned, $started, $finished, $trigger, $status, $failure, $read);
            """;
        Bind(command, run);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(ScheduledRun run, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scheduled_runs
            SET schedule_id = $schedule, schedule_title = $title, conversation_id = $conversation, planned_at = $planned,
                started_at = $started, finished_at = $finished, trigger = $trigger, status = $status,
                failure_text = $failure, read_at = $read
            WHERE id = $id;
            """;
        Bind(command, run);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<ScheduledRun?> GetAsync(string runId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM scheduled_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        return (await ReadAllAsync(command, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<ScheduledRun?> GetByConversationAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM scheduled_runs WHERE conversation_id = $conversation LIMIT 1;";
        command.Parameters.AddWithValue("$conversation", conversationId);
        return (await ReadAllAsync(command, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ScheduledRunGroup>> ListGroupedAsync(int perScheduleLimit, CancellationToken ct = default)
    {
        if (perScheduleLimit <= 0)
            return [];

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 視窗函式：每個排程內由新到舊編號，只取前 N 筆；latest 是該排程最新的計畫時間，組依它由新到舊排序。
        // 同一組的列在結果中相鄰（latest 與 schedule_id 都相同），所以 GroupBy 之後組內順序不變
        command.CommandText = $"""
            SELECT {Columns} FROM (
              SELECT {Columns},
                     ROW_NUMBER() OVER (PARTITION BY schedule_id ORDER BY planned_at DESC, rowid DESC) AS rn,
                     MAX(planned_at) OVER (PARTITION BY schedule_id) AS latest
              FROM scheduled_runs
            )
            WHERE rn <= $limit
            ORDER BY latest DESC, schedule_id, rn;
            """;
        command.Parameters.AddWithValue("$limit", perScheduleLimit);
        var runs = await ReadAllAsync(command, ct).ConfigureAwait(false);
        return runs
            .GroupBy(r => r.ScheduleId)
            .Select(g => new ScheduledRunGroup(g.Key, g.First().ScheduleTitle, g.ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<ScheduledRun>> ListForScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM scheduled_runs WHERE schedule_id = $schedule ORDER BY planned_at DESC, rowid DESC;";
        command.Parameters.AddWithValue("$schedule", scheduleId);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<int> CountUnreadAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 與 ScheduledRun.IsUnread 一致：沒讀過，而且不是執行中（待確認的算未讀）
        command.CommandText = "SELECT COUNT(*) FROM scheduled_runs WHERE read_at IS NULL AND status <> 'running';";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task MarkReadAsync(string runId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 已經有已讀時間就不改（第一次讀的時間保留）
        command.CommandText = "UPDATE scheduled_runs SET read_at = $at WHERE id = $id AND read_at IS NULL;";
        command.Parameters.AddWithValue("$at", FormatTime(at));
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduledRun>> ListBeyondAsync(string scheduleId, int keep, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 先依新舊編號（與 ListForScheduleAsync 相同順序），略過前 keep 筆，再排除執行中與待確認的
        command.CommandText = $"""
            SELECT {Columns} FROM (
              SELECT {Columns}, ROW_NUMBER() OVER (ORDER BY planned_at DESC, rowid DESC) AS rn
              FROM scheduled_runs WHERE schedule_id = $schedule
            )
            WHERE rn > $keep AND status NOT IN ({ActiveStatuses})
            ORDER BY rn;
            """;
        command.Parameters.AddWithValue("$schedule", scheduleId);
        command.Parameters.AddWithValue("$keep", keep);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string runId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scheduled_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", runId);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<int> MarkActiveInterruptedAsync(DateTimeOffset at, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE scheduled_runs SET status = 'interrupted', finished_at = $at WHERE status IN ({ActiveStatuses});";
        command.Parameters.AddWithValue("$at", FormatTime(at));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListOrphanRunConversationsAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id FROM conversations c
            WHERE c.kind = 'scheduled_run' AND NOT EXISTS (SELECT 1 FROM scheduled_runs r WHERE r.conversation_id = c.id);
            """;
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static void Bind(SqliteCommand command, ScheduledRun run)
    {
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$schedule", run.ScheduleId);
        command.Parameters.AddWithValue("$title", run.ScheduleTitle);
        command.Parameters.AddWithValue("$conversation", (object?)run.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$planned", FormatTime(run.PlannedAt));
        command.Parameters.AddWithValue("$started", run.StartedAt is { } started ? FormatTime(started) : DBNull.Value);
        command.Parameters.AddWithValue("$finished", run.FinishedAt is { } finished ? FormatTime(finished) : DBNull.Value);
        command.Parameters.AddWithValue("$trigger", FormatTrigger(run.Trigger));
        command.Parameters.AddWithValue("$status", FormatStatus(run.Status));
        command.Parameters.AddWithValue("$failure", (object?)run.FailureText ?? DBNull.Value);
        command.Parameters.AddWithValue("$read", run.ReadAt is { } read ? FormatTime(read) : DBNull.Value);
    }

    private static async Task<List<ScheduledRun>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var runs = new List<ScheduledRun>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            runs.Add(ReadRun(reader));
        return runs;
    }

    private static ScheduledRun ReadRun(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        ScheduleId: reader.GetString(1),
        ScheduleTitle: reader.GetString(2),
        ConversationId: reader.IsDBNull(3) ? null : reader.GetString(3),
        PlannedAt: ParseTime(reader.GetString(4)),
        StartedAt: reader.IsDBNull(5) ? null : ParseTime(reader.GetString(5)),
        FinishedAt: reader.IsDBNull(6) ? null : ParseTime(reader.GetString(6)),
        Trigger: ParseTrigger(reader.GetString(7)),
        Status: ParseStatus(reader.GetString(8)),
        FailureText: reader.IsDBNull(9) ? null : reader.GetString(9),
        ReadAt: reader.IsDBNull(10) ? null : ParseTime(reader.GetString(10)));

    private static string FormatTrigger(ScheduledRunTrigger trigger) => trigger switch
    {
        ScheduledRunTrigger.Scheduled => "scheduled",
        ScheduledRunTrigger.CatchUp => "catch_up",
        ScheduledRunTrigger.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
    };

    private static ScheduledRunTrigger ParseTrigger(string text) => text switch
    {
        "scheduled" => ScheduledRunTrigger.Scheduled,
        "catch_up" => ScheduledRunTrigger.CatchUp,
        "manual" => ScheduledRunTrigger.Manual,
        _ => throw new InvalidDataException($"未知的觸發方式：{text}"),
    };

    private static string FormatStatus(ScheduledRunStatus status) => status switch
    {
        ScheduledRunStatus.Running => "running",
        ScheduledRunStatus.AwaitingApproval => "awaiting_approval",
        ScheduledRunStatus.Completed => "completed",
        ScheduledRunStatus.Failed => "failed",
        ScheduledRunStatus.Missed => "missed",
        ScheduledRunStatus.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static ScheduledRunStatus ParseStatus(string text) => text switch
    {
        "running" => ScheduledRunStatus.Running,
        "awaiting_approval" => ScheduledRunStatus.AwaitingApproval,
        "completed" => ScheduledRunStatus.Completed,
        "failed" => ScheduledRunStatus.Failed,
        "missed" => ScheduledRunStatus.Missed,
        "interrupted" => ScheduledRunStatus.Interrupted,
        _ => throw new InvalidDataException($"未知的排程紀錄狀態：{text}"),
    };

    // 一律存 UTC、固定格式（與 SqliteScheduledItemStore、SqliteConversationStore 相同）
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
