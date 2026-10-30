using System.Globalization;
using Lumi.Core.Usage;
using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUsageHintLog"/> 的 SQLite 實作（資料表 <c>usage_hints</c>，migration 12）。
/// 和 <see cref="SqliteUsageStore"/> 共用同一個資料庫與初始化（連線與 schema 由 <see cref="SqliteConversationStore"/> 負責）。
/// 時間存 UTC 固定格式；<c>conversation_id</c> 不設外鍵，刪除對話不刪紀錄。
/// </summary>
internal sealed class SqliteUsageHintLog(SqliteConversationStore database) : IUsageHintLog
{
    public async Task AddAsync(UsageHintEntry entry, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO usage_hints (id, rule, conversation_id, created_at, outcome)
            VALUES ($id, $rule, $conversation, $created, $outcome);
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$rule", entry.Rule);
        command.Parameters.AddWithValue("$conversation", (object?)entry.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", FormatTime(entry.CreatedAt));
        command.Parameters.AddWithValue("$outcome", FormatOutcome(entry.Outcome));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetOutcomeAsync(string id, UsageHintOutcome outcome, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE usage_hints SET outcome = $outcome WHERE id = $id;";
        command.Parameters.AddWithValue("$outcome", FormatOutcome(outcome));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> WasShownAsync(string rule, string conversationId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM usage_hints
                WHERE rule = $rule AND conversation_id = $conversation AND outcome <> $suppressed);
            """;
        command.Parameters.AddWithValue("$rule", rule);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$suppressed", FormatOutcome(UsageHintOutcome.Suppressed));
        var exists = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(exists, CultureInfo.InvariantCulture) != 0;
    }

    public async Task<DateTimeOffset?> LastShownAsync(string rule, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(created_at) FROM usage_hints WHERE rule = $rule AND outcome <> $suppressed;";
        command.Parameters.AddWithValue("$rule", rule);
        command.Parameters.AddWithValue("$suppressed", FormatOutcome(UsageHintOutcome.Suppressed));
        var max = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return max is string text ? ParseTime(text) : null;
    }

    public async Task<IReadOnlyList<UsageHintEntry>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, rule, conversation_id, created_at, outcome FROM usage_hints
            WHERE created_at >= $from AND created_at < $to ORDER BY created_at, id;
            """;
        command.Parameters.AddWithValue("$from", FormatTime(from));
        command.Parameters.AddWithValue("$to", FormatTime(to));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<UsageHintEntry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new UsageHintEntry(
                Id: reader.GetString(0),
                Rule: reader.GetString(1),
                ConversationId: reader.IsDBNull(2) ? null : reader.GetString(2),
                CreatedAt: ParseTime(reader.GetString(3)),
                Outcome: ParseOutcome(reader.GetString(4))));
        }
        return list;
    }

    private static string FormatOutcome(UsageHintOutcome outcome) => outcome switch
    {
        UsageHintOutcome.Shown => "shown",
        UsageHintOutcome.Dismissed => "dismissed",
        UsageHintOutcome.Muted => "muted",
        UsageHintOutcome.Acted => "acted",
        UsageHintOutcome.Suppressed => "suppressed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    private static UsageHintOutcome ParseOutcome(string text) => text switch
    {
        "shown" => UsageHintOutcome.Shown,
        "dismissed" => UsageHintOutcome.Dismissed,
        "muted" => UsageHintOutcome.Muted,
        "acted" => UsageHintOutcome.Acted,
        "suppressed" => UsageHintOutcome.Suppressed,
        _ => throw new InvalidDataException($"未知的提示結果：{text}"),
    };

    // 一律存 UTC、固定格式（同 SqliteUsageStore）
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
