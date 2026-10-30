using System.Globalization;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Usage;
using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IUsageStore"/> 的 SQLite 實作（資料表 <c>request_usage</c>，migration 12）。
/// 和 <see cref="SqliteMemoryStore"/> 共用同一個資料庫與初始化（連線與 schema 由 <see cref="SqliteConversationStore"/> 負責）。
/// 時間存 UTC 固定格式，字串比較就是時間比較；<c>conversation_id</c> 不設外鍵，刪除對話不刪費用。
/// </summary>
internal sealed class SqliteUsageStore(SqliteConversationStore database) : IUsageStore
{
    private const string Columns =
        "id, created_at, conversation_id, purpose, mode, model_setting_id, model_id, connection_name, " +
        "input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, usage_estimated, " +
        "price_snapshot_json, cost_usd, tool_definition_tokens";

    public async Task AddAsync(UsageRecord record, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO request_usage (id, created_at, conversation_id, purpose, mode, model_setting_id, model_id, connection_name,
                input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, usage_estimated,
                price_snapshot_json, cost_usd, tool_definition_tokens)
            VALUES ($id, $created, $conversation, $purpose, $mode, $setting, $model, $connection,
                $input, $output, $cacheCreation, $cacheRead, $estimated, $price, $cost, $toolTokens);
            """;
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$created", FormatTime(record.CreatedAt));
        command.Parameters.AddWithValue("$conversation", (object?)record.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$purpose", FormatPurpose(record.Purpose));
        command.Parameters.AddWithValue("$mode", FormatMode(record.Mode));
        command.Parameters.AddWithValue("$setting", (object?)record.ModelSettingsId ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", record.ModelId);
        command.Parameters.AddWithValue("$connection", record.ConnectionName);
        command.Parameters.AddWithValue("$input", record.Tokens.InputTokens);
        command.Parameters.AddWithValue("$output", record.Tokens.OutputTokens);
        command.Parameters.AddWithValue("$cacheCreation", record.Tokens.CacheCreationInputTokens);
        command.Parameters.AddWithValue("$cacheRead", record.Tokens.CacheReadInputTokens);
        command.Parameters.AddWithValue("$estimated", record.Estimated ? 1 : 0);
        command.Parameters.AddWithValue("$price", record.Price is { } price ? JsonSerializer.Serialize(price) : DBNull.Value);
        // 費用存成 REAL（decimal 不是 SQLite 的原生型別）；null＝未設定單價
        command.Parameters.AddWithValue("$cost", record.CostUsd is { } cost ? (double)cost : DBNull.Value);
        command.Parameters.AddWithValue("$toolTokens", record.ToolDefinitionTokens);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UsageRecord>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM request_usage WHERE created_at >= $from AND created_at < $to ORDER BY created_at, id;";
        command.Parameters.AddWithValue("$from", FormatTime(from));
        command.Parameters.AddWithValue("$to", FormatTime(to));
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<UsageRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(ReadRecord(reader));
        return list;
    }

    public async Task<ConversationCost> GetConversationCostAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COUNT(cost_usd), COALESCE(SUM(cost_usd), 0), COALESCE(MAX(usage_estimated), 0)
            FROM request_usage WHERE conversation_id = $conversation;
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);

        var total = reader.GetInt64(0);
        if (total == 0)
            return ConversationCost.Empty;
        var priced = reader.GetInt64(1);
        return new ConversationCost(
            priced == 0 ? null : (decimal)reader.GetDouble(2),
            reader.GetInt64(3) != 0,
            (int)(total - priced));
    }

    public async Task<IReadOnlyList<ConversationCostItem>> ListTopConversationsAsync(
        DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // INNER JOIN：已刪除的對話不列。只列有單價的對話（全部沒有單價的對話，費用是 null，不排名）。
        // 回合數用這則對話目前的使用者訊息數（不限期間）
        command.CommandText = """
            SELECT u.conversation_id, c.title, MAX(u.created_at), SUM(u.cost_usd), MAX(u.usage_estimated),
                   (SELECT COUNT(*) FROM messages m WHERE m.conversation_id = u.conversation_id AND m.role = $userRole)
            FROM request_usage u
            INNER JOIN conversations c ON c.id = u.conversation_id
            WHERE u.created_at >= $from AND u.created_at < $to
            GROUP BY u.conversation_id, c.title
            HAVING COUNT(u.cost_usd) > 0
            ORDER BY SUM(u.cost_usd) DESC, u.conversation_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$userRole", "user");   // 同 SqliteConversationStore 的 FormatRole(ChatRole.User)
        command.Parameters.AddWithValue("$from", FormatTime(from));
        command.Parameters.AddWithValue("$to", FormatTime(to));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<ConversationCostItem>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new ConversationCostItem(
                ConversationId: reader.GetString(0),
                Title: reader.GetString(1),
                LastAt: ParseTime(reader.GetString(2)),
                TurnCount: reader.GetInt32(5),
                CostUsd: (decimal)reader.GetDouble(3),
                HasEstimated: reader.GetInt64(4) != 0));
        }
        return list;
    }

    public async Task<decimal> SumCostAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(cost_usd), 0) FROM request_usage WHERE created_at >= $from AND created_at < $to;";
        command.Parameters.AddWithValue("$from", FormatTime(from));
        command.Parameters.AddWithValue("$to", FormatTime(to));
        var sum = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (decimal)Convert.ToDouble(sum, CultureInfo.InvariantCulture);
    }

    private static UsageRecord ReadRecord(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        CreatedAt: ParseTime(reader.GetString(1)),
        ConversationId: reader.IsDBNull(2) ? null : reader.GetString(2),
        Purpose: ParsePurpose(reader.GetString(3)),
        Mode: ParseMode(reader.GetString(4)),
        ModelSettingsId: reader.IsDBNull(5) ? null : reader.GetString(5),
        ModelId: reader.GetString(6),
        ConnectionName: reader.GetString(7),
        Tokens: new TokenUsage(reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11)),
        Estimated: reader.GetInt64(12) != 0,
        Price: reader.IsDBNull(13) ? null : JsonSerializer.Deserialize<PriceSnapshot>(reader.GetString(13)),
        CostUsd: reader.IsDBNull(14) ? null : (decimal)reader.GetDouble(14),
        ToolDefinitionTokens: reader.GetInt32(15));

    private static string FormatPurpose(UsagePurpose purpose) => purpose switch
    {
        UsagePurpose.Chat => "chat",
        UsagePurpose.Compaction => "compaction",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null),
    };

    private static UsagePurpose ParsePurpose(string text) => text switch
    {
        "chat" => UsagePurpose.Chat,
        "compaction" => UsagePurpose.Compaction,
        _ => throw new InvalidDataException($"未知的用量用途：{text}"),
    };

    private static string FormatMode(ChatMode mode) => mode switch
    {
        ChatMode.Workbench => "workbench",
        ChatMode.Sprite => "sprite",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static ChatMode ParseMode(string text) => text switch
    {
        "workbench" => ChatMode.Workbench,
        "sprite" => ChatMode.Sprite,
        _ => throw new InvalidDataException($"未知的用量模式：{text}"),
    };

    // 一律存 UTC、固定格式（同 SqliteConversationStore）
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
