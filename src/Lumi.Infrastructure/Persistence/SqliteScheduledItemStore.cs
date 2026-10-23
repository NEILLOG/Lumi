using System.Globalization;
using System.Text.Json;
using Lumi.Core.Reminders;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IScheduledItemStore"/> 的 SQLite 實作（資料表 <c>scheduled_items</c>，migration 5）。
/// 和 <see cref="SqliteConversationStore"/> 共用同一個資料庫與初始化（連線與 schema 由它負責）。
/// 讀取時遇到無法還原的列（例如未知的 trigger <c>type</c>）只記 log 並略過，不讓整個列表失敗；
/// 略過的列仍算在 <see cref="CountPendingAsync"/> 裡（它還佔著上限），但不會被觸發。
/// </summary>
public sealed class SqliteScheduledItemStore(SqliteConversationStore database, ILogger<SqliteScheduledItemStore> logger) : IScheduledItemStore
{
    private const string Columns =
        "id, kind, title, message, trigger_json, time_zone_id, action_json, status, enabled, next_fire_at, last_fire_at, created_by, created_at";

    // 排序鍵與索引欄位一致；時間都是 UTC 固定格式，字串排序等於時間排序
    private const string SortKey = "COALESCE(next_fire_at, created_at)";

    public async Task AddAsync(ScheduledItem item, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO scheduled_items ({Columns})
            VALUES ($id, $kind, $title, $message, $trigger, $timeZone, $action, $status, $enabled, $nextFireAt, $lastFireAt, $createdBy, $createdAt);
            """;
        Bind(command, item);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> UpdateAsync(ScheduledItem item, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scheduled_items
            SET kind = $kind, title = $title, message = $message, trigger_json = $trigger, time_zone_id = $timeZone,
                action_json = $action, status = $status, enabled = $enabled, next_fire_at = $nextFireAt,
                last_fire_at = $lastFireAt, created_by = $createdBy, created_at = $createdAt
            WHERE id = $id;
            """;
        Bind(command, item);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<ScheduledItem?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM scheduled_items WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (await ReadAllAsync(command, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ScheduledItem>> ListAsync(ScheduledItemFilter filter, CancellationToken ct = default)
    {
        if (filter.Limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(filter), "Limit 必須 ≥ 1。");
        if (filter.Statuses is { Count: 0 } || filter.Kinds is { Count: 0 })
            return [];

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        if (filter.Statuses is not null)
            conditions.Add($"status IN ({AddIntParameters(command, "$status", filter.Statuses.Select(s => (int)s))})");
        if (filter.Kinds is not null)
            conditions.Add($"kind IN ({AddIntParameters(command, "$kind", filter.Kinds.Select(k => (int)k))})");

        var where = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
        var limit = filter.Limit is { } n ? "LIMIT $limit" : "";
        if (filter.Limit is { } max)
            command.Parameters.AddWithValue("$limit", max);
        command.CommandText = $"SELECT {Columns} FROM scheduled_items {where} ORDER BY {SortKey}, id {limit};";
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM scheduled_items WHERE status = $pending;";
        command.Parameters.AddWithValue("$pending", (int)ScheduledStatus.Pending);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<ScheduledItem>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM scheduled_items
            WHERE enabled = 1 AND status = $pending AND next_fire_at <= $now
            ORDER BY next_fire_at, id;
            """;
        command.Parameters.AddWithValue("$pending", (int)ScheduledStatus.Pending);
        command.Parameters.AddWithValue("$now", FormatTime(now));
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset?> GetNextFireAtAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // MIN 忽略 NULL；沒有任何符合的列時回 NULL
        command.CommandText = "SELECT MIN(next_fire_at) FROM scheduled_items WHERE enabled = 1 AND status = $pending;";
        command.Parameters.AddWithValue("$pending", (int)ScheduledStatus.Pending);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is string text ? ParseTime(text) : null;
    }

    public async Task<int> PurgeFinishedAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 已結束的項目以最後觸發時間為準，沒有就用下次時間，再沒有才用建立時間（與 InMemoryScheduledItemStore 相同）
        command.CommandText = $"""
            DELETE FROM scheduled_items
            WHERE status IN ({(int)ScheduledStatus.Completed}, {(int)ScheduledStatus.Cancelled}, {(int)ScheduledStatus.Missed})
              AND COALESCE(last_fire_at, next_fire_at, created_at) < $olderThan;
            """;
        command.Parameters.AddWithValue("$olderThan", FormatTime(olderThan));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void Bind(SqliteCommand command, ScheduledItem item)
    {
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$message", item.Message);
        command.Parameters.AddWithValue("$trigger", JsonSerializer.Serialize(item.Trigger));
        command.Parameters.AddWithValue("$timeZone", item.TimeZoneId);
        command.Parameters.AddWithValue("$action", JsonSerializer.Serialize(item.Action));
        command.Parameters.AddWithValue("$status", (int)item.Status);
        command.Parameters.AddWithValue("$enabled", item.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$nextFireAt", item.NextFireAt is { } next ? FormatTime(next) : DBNull.Value);
        command.Parameters.AddWithValue("$lastFireAt", item.LastFiredAt is { } last ? FormatTime(last) : DBNull.Value);
        command.Parameters.AddWithValue("$createdBy", item.CreatedBy.ToString());
        command.Parameters.AddWithValue("$createdAt", FormatTime(item.CreatedAt));
    }

    /// <summary>加上 <c>$prefix0</c>、<c>$prefix1</c>… 參數，回傳逗號分隔的佔位字串（給 IN (...) 用）。</summary>
    private static string AddIntParameters(SqliteCommand command, string prefix, IEnumerable<int> values)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = $"{prefix}{names.Count}";
            command.Parameters.AddWithValue(name, value);
            names.Add(name);
        }
        return string.Join(", ", names);
    }

    private async Task<List<ScheduledItem>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var items = new List<ScheduledItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            try
            {
                items.Add(ReadItem(reader));
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidDataException or FormatException or ArgumentException)
            {
                // 只記 Id，不記內容（內容可能含使用者的文字）
                logger.LogWarning(e, "排程項目 {Id} 無法讀取，已略過", id);
            }
        }
        return items;
    }

    private static ScheduledItem ReadItem(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Kind: (ScheduledKind)reader.GetInt32(1),
        Title: reader.GetString(2),
        Message: reader.GetString(3),
        Trigger: JsonSerializer.Deserialize<Trigger>(reader.GetString(4)) ?? throw new InvalidDataException("trigger_json 為 null。"),
        TimeZoneId: reader.GetString(5),
        Action: JsonSerializer.Deserialize<ScheduledAction>(reader.GetString(6)) ?? throw new InvalidDataException("action_json 為 null。"),
        Status: (ScheduledStatus)reader.GetInt32(7),
        Enabled: reader.GetInt32(8) != 0,
        NextFireAt: reader.IsDBNull(9) ? null : ParseTime(reader.GetString(9)),
        LastFiredAt: reader.IsDBNull(10) ? null : ParseTime(reader.GetString(10)),
        CreatedBy: Enum.Parse<ScheduledCreator>(reader.GetString(11)),
        CreatedAt: ParseTime(reader.GetString(12)));

    // 一律存 UTC、固定格式（與 SqliteConversationStore 相同）
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
