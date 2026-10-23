using System.Data;
using System.Globalization;
using Lumi.Core.Memory;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IMemoryStore"/> 的 SQLite 實作（資料表 <c>memories</c>，migration 6）。
/// 和 <see cref="SqliteConversationStore"/> 共用同一個資料庫與初始化（連線與 schema 由它負責）。
/// 新增與修改都在 <b>IMMEDIATE</b> 交易內進行：一開始就拿到寫入鎖，讀出既有資料、檢查上限、寫入之間不會有別的寫入插進來，
/// 所以兩個同時新增不會突破上限。規則全部走 <see cref="MemoryWriteRules"/>／<see cref="MemoryRules"/>，和記憶體版一致。
/// 讀取時遇到無法還原的列只記 log 並略過（不讓整個列表失敗）。
/// </summary>
public sealed class SqliteMemoryStore(
    SqliteConversationStore database,
    MemoryOptions options,
    TimeProvider time,
    ILogger<SqliteMemoryStore> logger) : IMemoryStore
{
    private const string Columns =
        "id, content, category, source, source_conversation_id, created_at, updated_at, expires_at";

    private readonly object _changeLock = new();
    private CancellationTokenSource _changes = new();

    /// <inheritdoc />
    public CancellationToken ChangeToken
    {
        get
        {
            lock (_changeLock)
                return _changes.Token;
        }
    }

    public async Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        return await ReadAllAsync(connection, null, "", ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryItem>> ListActiveAsync(DateOnly today, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        var all = await ReadAllAsync(connection, null, "", ct).ConfigureAwait(false);
        return all.Where(item => MemoryRules.IsActive(item, today)).ToList();
    }

    public async Task<MemoryItem?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        return (await ReadAllAsync(connection, null, "WHERE id = $id", ct, command => command.Parameters.AddWithValue("$id", id)).ConfigureAwait(false))
            .FirstOrDefault();
    }

    /// <summary>
    /// 同步讀一則。<c>ITool.Preview</c> 是同步的，只能在這裡等；本機 SQLite 的單筆讀取很快，
    /// 而且 <see cref="SqliteConversationStore.OpenAsync"/> 全程 ConfigureAwait(false)，等待不會卡住 UI 執行緒。
    /// </summary>
    public MemoryItem? TryGet(int id) => GetAsync(id, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<MemoryAddResult> AddAsync(NewMemory memory, CancellationToken ct = default)
    {
        var normalized = MemoryRules.NormalizeContent(memory.Content);
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var existing = await ReadAllAsync(connection, transaction, "", ct).ConfigureAwait(false);
        var rejected = MemoryWriteRules.CheckAdd(normalized, memory.Category, memory.Source, existing, options);
        if (rejected is not null)
            return rejected;   // 交易沒有 Commit，釋放時回滾（什麼都沒寫）

        var now = time.GetUtcNow();
        long id;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO memories (content, category, source, source_conversation_id, created_at, updated_at, expires_at)
                VALUES ($content, $category, $source, $conversation, $created, $updated, $expires)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$content", normalized);
            insert.Parameters.AddWithValue("$category", (int)memory.Category);
            insert.Parameters.AddWithValue("$source", (int)memory.Source);
            insert.Parameters.AddWithValue("$conversation", (object?)memory.SourceConversationId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", FormatTime(now));
            insert.Parameters.AddWithValue("$updated", FormatTime(now));
            insert.Parameters.AddWithValue("$expires", memory.ExpiresAt is { } expires ? FormatDate(expires) : DBNull.Value);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        SignalChange();
        return new MemoryAddResult(
            MemoryAddOutcome.Added,
            new MemoryItem((int)id, normalized, memory.Category, memory.Source, memory.SourceConversationId, now, now, memory.ExpiresAt),
            null);
    }

    public async Task<MemoryUpdateResult> UpdateAsync(int id, MemoryPatch patch, CancellationToken ct = default)
    {
        var normalized = patch.Content is null ? null : MemoryRules.NormalizeContent(patch.Content);
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);

        var existing = await ReadAllAsync(connection, transaction, "", ct).ConfigureAwait(false);
        var target = existing.FirstOrDefault(item => item.Id == id);
        var rejected = MemoryWriteRules.CheckUpdate(id, target, patch, normalized, existing, options);
        if (rejected is not null)
            return rejected;

        var updated = MemoryWriteRules.Apply(target!, patch, normalized, time.GetUtcNow());
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE memories
                SET content = $content, category = $category, expires_at = $expires, updated_at = $updated
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$content", updated.Content);
            command.Parameters.AddWithValue("$category", (int)updated.Category);
            command.Parameters.AddWithValue("$expires", updated.ExpiresAt is { } expires ? FormatDate(expires) : DBNull.Value);
            command.Parameters.AddWithValue("$updated", FormatTime(updated.UpdatedAt));
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        SignalChange();
        return new MemoryUpdateResult(MemoryUpdateOutcome.Updated, updated, null);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memories WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        var removed = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        if (removed)
            SignalChange();
        return removed;
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memories;";
        var count = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (count > 0)
            SignalChange();
        return count;
    }

    public async Task<int> PurgeExpiredAsync(DateOnly today, int graceDays, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // yyyy-MM-dd 的字串排序等於日期排序；剛好等於界線的不刪（嚴格小於）
        command.CommandText = "DELETE FROM memories WHERE expires_at IS NOT NULL AND expires_at < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", FormatDate(today.AddDays(-graceDays)));
        var count = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (count > 0)
            SignalChange();
        return count;
    }

    /// <summary>
    /// 讀出符合條件的列，排序與記憶體版相同（分類再 id）。
    /// 無法還原的列只記 log（只記 id，不記內容）並略過。
    /// </summary>
    private async Task<List<MemoryItem>> ReadAllAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string where,
        CancellationToken ct,
        Action<SqliteCommand>? configure = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM memories {where} ORDER BY category, id;";
        configure?.Invoke(command);

        var items = new List<MemoryItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetInt64(0);
            try
            {
                items.Add(ReadItem(reader));
            }
            catch (Exception e) when (e is InvalidDataException or FormatException or ArgumentException or InvalidCastException)
            {
                logger.LogWarning(e, "長期記憶 {Id} 無法讀取，已略過", id);
            }
        }
        return items;
    }

    private static MemoryItem ReadItem(SqliteDataReader reader) => new(
        Id: reader.GetInt32(0),
        Content: reader.GetString(1),
        Category: ParseEnum<MemoryCategory>(reader.GetInt32(2), "category"),
        Source: ParseEnum<MemorySource>(reader.GetInt32(3), "source"),
        SourceConversationId: reader.IsDBNull(4) ? null : reader.GetString(4),
        CreatedAt: ParseTime(reader.GetString(5)),
        UpdatedAt: ParseTime(reader.GetString(6)),
        ExpiresAt: reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)));

    private static T ParseEnum<T>(int value, string column) where T : struct, Enum =>
        Enum.IsDefined(typeof(T), value) ? (T)Enum.ToObject(typeof(T), value) : throw new InvalidDataException($"memories.{column} 的值不合法：{value}");

    /// <summary>有變動才呼叫：換成新的權杖，並在鎖外取消舊的（同 <c>ReminderService</c>）。</summary>
    private void SignalChange()
    {
        CancellationTokenSource previous;
        lock (_changeLock)
        {
            previous = _changes;
            _changes = new CancellationTokenSource();
        }
        previous.Cancel();
    }

    // 一律存 UTC、固定格式（與 SqliteScheduledItemStore 相同）
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDate(string text) =>
        DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
