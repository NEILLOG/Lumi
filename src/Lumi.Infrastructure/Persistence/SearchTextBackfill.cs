using System.Text.Json;
using Lumi.Core.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// 把 migration 4 之前的訊息補上 <c>search_text</c>（從內容的文字區塊取出，見 <see cref="SearchTextExtractor"/>）。
/// 分批處理，每批一個交易，批與批之間檢查取消；中途取消或失敗不記旗標，下次啟動從頭再跑——
/// 已經有 search_text 的列會被略過，所以重跑安全。全部跑完後在 <c>app_meta</c> 記「已補完」，之後的啟動直接略過。
/// 旗標是必要的：純圖片、純工具的訊息本來就沒有文字，search_text 會一直是空的，不能靠「空的就重掃」判斷。
/// 新寫入的訊息由 <see cref="SqliteConversationStore.AppendMessageAsync"/> 直接填好，不需要補。
/// </summary>
public sealed class SearchTextBackfill(SqliteConversationStore store, ILogger<SearchTextBackfill> logger)
{
    internal const string DoneFlagKey = "search_text_backfilled";
    internal const int BatchSize = 200;

    /// <returns>這次補上了幾則有文字的訊息；已經補完過回 0。</returns>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await using var connection = await store.OpenAsync(ct).ConfigureAwait(false);
        if (await IsDoneAsync(connection, ct).ConfigureAwait(false))
            return 0;

        var filled = 0;
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await ReadBatchAsync(connection, after, ct).ConfigureAwait(false);
            if (batch.Count == 0)
                break;

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var (rowId, json) in batch)
            {
                var text = ExtractOrNull(rowId, json);
                if (string.IsNullOrEmpty(text))
                    continue;   // 純圖片或純工具：本來就沒有文字，維持空字串

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE messages SET search_text = $text WHERE rowid = $rowid AND search_text = '';";
                update.Parameters.AddWithValue("$text", text);
                update.Parameters.AddWithValue("$rowid", rowId);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                filled++;
            }
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            after = batch[^1].RowId;
        }

        await using (var flag = connection.CreateCommand())
        {
            flag.CommandText = "INSERT OR REPLACE INTO app_meta (key, value) VALUES ($key, '1');";
            flag.Parameters.AddWithValue("$key", DoneFlagKey);
            await flag.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        if (filled > 0)
            logger.LogInformation("已補上 {Count} 則訊息的搜尋文字", filled);
        return filled;
    }

    private static async Task<bool> IsDoneAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_meta WHERE key = $key AND value = '1';";
        command.Parameters.AddWithValue("$key", DoneFlagKey);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }

    private static async Task<List<(long RowId, string Json)>> ReadBatchAsync(SqliteConnection connection, long after, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rowid, content_json FROM messages
            WHERE rowid > $after AND search_text = ''
            ORDER BY rowid LIMIT $batch;
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$batch", BatchSize);

        var batch = new List<(long, string)>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            batch.Add((reader.GetInt64(0), reader.GetString(1)));
        return batch;
    }

    /// <summary>內容讀不出來的訊息只記 log 並略過（它本來就讀不回來，不能因為它讓整批補不完）。</summary>
    private string? ExtractOrNull(long rowId, string json)
    {
        try
        {
            var content = JsonSerializer.Deserialize<List<ContentBlock>>(json) ?? [];
            return SearchTextExtractor.Extract(content);
        }
        catch (JsonException e)
        {
            logger.LogWarning(e, "訊息（rowid {RowId}）的內容無法讀取，略過搜尋文字的補填", rowId);
            return null;
        }
    }
}
