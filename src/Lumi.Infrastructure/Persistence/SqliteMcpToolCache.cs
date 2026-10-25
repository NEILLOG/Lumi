using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumi.Core.Mcp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IMcpToolCache"/> 的 SQLite 實作（資料表 <c>mcp_tool_cache</c>，migration 10）。
/// 每個伺服器一列，<c>tools_json</c> 是 <see cref="McpCachedTool"/> 陣列的 JSON。
/// 和 <see cref="SqliteConversationStore"/> 共用同一個資料庫與初始化（連線與 schema 由它負責）。
/// 讀到無法還原的內容只記 log 並當作沒有快取（不讓工具清單頁失敗）。
/// </summary>
public sealed class SqliteMcpToolCache(
    SqliteConversationStore database,
    TimeProvider time,
    ILogger<SqliteMcpToolCache> logger) : IMcpToolCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<IReadOnlyList<McpCachedTool>> GetAsync(string serverId, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tools_json FROM mcp_tool_cache WHERE server_id = $id;";
        command.Parameters.AddWithValue("$id", serverId);
        var json = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (json is null)
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<McpCachedTool>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // 不記錄例外內容：工具的描述可能很長；快取壞掉就當作沒有，下次連上會重寫
            logger.LogWarning("MCP 伺服器 {ServerId} 的工具快取格式錯誤，已忽略", serverId);
            return [];
        }
    }

    public async Task SaveAsync(string serverId, IReadOnlyList<McpCachedTool> tools, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(tools, JsonOptions);
        var updatedAt = time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mcp_tool_cache (server_id, tools_json, updated_at)
            VALUES ($id, $tools, $updated)
            ON CONFLICT(server_id) DO UPDATE SET tools_json = excluded.tools_json, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", serverId);
        command.Parameters.AddWithValue("$tools", json);
        command.Parameters.AddWithValue("$updated", updatedAt);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string serverId, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mcp_tool_cache WHERE server_id = $id;";
        command.Parameters.AddWithValue("$id", serverId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
