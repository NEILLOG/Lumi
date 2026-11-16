using System.Globalization;
using System.Text;
using Lumi.Core.Branches;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.Branches;

/// <summary>
/// <see cref="IBranchStore"/> 的 SQLite 實作（資料表 <c>conversation_branches</c>，migration 15）。
/// 和 <see cref="SqliteConversationStore"/> 共用同一個資料庫與初始化；刪除主線時靠外鍵 ON DELETE CASCADE 刪掉支線列（支線對話本身由刪除服務先處理，見 4.9）。
/// status 存小寫字串，時間存 UTC 固定格式（同 <see cref="SqliteContextNoteStore"/>）。
/// </summary>
public sealed class SqliteBranchStore(SqliteConversationStore database) : IBranchStore
{
    private const string Columns =
        "conversation_id, parent_id, root_id, depth, fork_message_id, fork_excerpt, fork_message_at, quote, status, summary, delivered_message_id, created_at, ended_at";

    // SQLite 的 PRIMARY KEY／UNIQUE 違反（同一則對話只能是一條支線）
    private const int SqliteConstraintErrorCode = 19;
    private const int SqlitePrimaryKeyConstraint = 1555;
    private const int SqliteUniqueConstraint = 2067;

    public async Task AddAsync(Branch branch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(branch);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO conversation_branches ({Columns})
            VALUES ($conversation, $parent, $root, $depth, $forkMessage, $excerpt, $forkAt, $quote, $status, $summary, $delivered, $created, $ended);
            """;
        command.Parameters.AddWithValue("$conversation", branch.ConversationId);
        command.Parameters.AddWithValue("$parent", branch.ParentId);
        command.Parameters.AddWithValue("$root", branch.RootId);
        command.Parameters.AddWithValue("$depth", branch.Depth);
        command.Parameters.AddWithValue("$forkMessage", branch.ForkMessageId);
        command.Parameters.AddWithValue("$excerpt", branch.ForkExcerpt);
        command.Parameters.AddWithValue("$forkAt", FormatTime(branch.ForkMessageAt));
        command.Parameters.AddWithValue("$quote", (object?)branch.Quote ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", FormatStatus(branch.Status));
        command.Parameters.AddWithValue("$summary", (object?)branch.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$delivered", (object?)branch.DeliveredMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", FormatTime(branch.CreatedAt));
        command.Parameters.AddWithValue("$ended", branch.EndedAt is { } ended ? FormatTime(ended) : DBNull.Value);
        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraintErrorCode && ex.SqliteExtendedErrorCode is SqlitePrimaryKeyConstraint or SqliteUniqueConstraint)
        {
            // 這則對話已經是支線（conversation_id 是主鍵）
            throw new InvalidOperationException("這則對話已經是支線。", ex);
        }
    }

    public async Task<Branch?> GetAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM conversation_branches WHERE conversation_id = $id;";
        command.Parameters.AddWithValue("$id", conversationId);
        return (await ReadAllAsync(command, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<Branch>> ListChildrenAsync(string parentId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM conversation_branches
            WHERE parent_id = $parent ORDER BY created_at, conversation_id;
            """;
        command.Parameters.AddWithValue("$parent", parentId);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Branch>> ListByRootsAsync(IReadOnlyCollection<string> rootIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootIds);
        if (rootIds.Count == 0)
            return [];

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 每個 id 一個參數，不拼字串
        var placeholders = new StringBuilder();
        var index = 0;
        foreach (var rootId in rootIds)
        {
            if (index > 0)
                placeholders.Append(", ");
            var name = "$root" + index.ToString(CultureInfo.InvariantCulture);
            placeholders.Append(name);
            command.Parameters.AddWithValue(name, rootId);
            index++;
        }
        command.CommandText = $"""
            SELECT {Columns} FROM conversation_branches
            WHERE root_id IN ({placeholders})
            ORDER BY root_id, created_at, conversation_id;
            """;
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task<bool> MergeAsync(string conversationId, string summary, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 只在進行中時改；已經結束的不動
        command.CommandText = """
            UPDATE conversation_branches SET status = 'merged', summary = $summary, ended_at = $ended
            WHERE conversation_id = $id AND status = 'active';
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$ended", FormatTime(endedAt));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<bool> AbandonAsync(string conversationId, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE conversation_branches SET status = 'abandoned', ended_at = $ended
            WHERE conversation_id = $id AND status = 'active';
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        command.Parameters.AddWithValue("$ended", FormatTime(endedAt));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<Branch>> ListUndeliveredAsync(string parentId, CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 時間是固定格式的 UTC 字串，字串排序就是時間排序
        command.CommandText = $"""
            SELECT {Columns} FROM conversation_branches
            WHERE parent_id = $parent AND status = 'merged' AND delivered_message_id IS NULL
            ORDER BY ended_at, conversation_id;
            """;
        command.Parameters.AddWithValue("$parent", parentId);
        return await ReadAllAsync(command, ct).ConfigureAwait(false);
    }

    public async Task MarkDeliveredAsync(IReadOnlyCollection<string> conversationIds, string messageId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);

        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE conversation_branches SET delivered_message_id = $message
                WHERE conversation_id = $id AND delivered_message_id IS NULL;
                """;
            var message = command.Parameters.Add("$message", SqliteType.Text);
            var id = command.Parameters.Add("$id", SqliteType.Text);
            message.Value = messageId;
            foreach (var conversationId in conversationIds)
            {
                id.Value = conversationId;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListOrphanConversationsAsync(CancellationToken ct = default)
    {
        await using var connection = await database.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id FROM conversations c
            WHERE c.kind = 'branch' AND NOT EXISTS (SELECT 1 FROM conversation_branches b WHERE b.conversation_id = c.id);
            """;
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            ids.Add(reader.GetString(0));
        return ids;
    }

    private static async Task<List<Branch>> ReadAllAsync(SqliteCommand command, CancellationToken ct)
    {
        var branches = new List<Branch>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            branches.Add(new Branch(
                ConversationId: reader.GetString(0),
                ParentId: reader.GetString(1),
                RootId: reader.GetString(2),
                Depth: reader.GetInt32(3),
                ForkMessageId: reader.GetString(4),
                ForkExcerpt: reader.GetString(5),
                ForkMessageAt: ParseTime(reader.GetString(6)),
                Quote: reader.IsDBNull(7) ? null : reader.GetString(7),
                Status: ParseStatus(reader.GetString(8)),
                Summary: reader.IsDBNull(9) ? null : reader.GetString(9),
                DeliveredMessageId: reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt: ParseTime(reader.GetString(11)),
                EndedAt: reader.IsDBNull(12) ? null : ParseTime(reader.GetString(12))));
        }
        return branches;
    }

    private static string FormatStatus(BranchStatus status) => status switch
    {
        BranchStatus.Active => "active",
        BranchStatus.Merged => "merged",
        BranchStatus.Abandoned => "abandoned",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static BranchStatus ParseStatus(string text) => text switch
    {
        "active" => BranchStatus.Active,
        "merged" => BranchStatus.Merged,
        "abandoned" => BranchStatus.Abandoned,
        _ => throw new InvalidOperationException($"未知的支線狀態：{text}"),
    };

    // 一律存 UTC、固定格式（與 SqliteContextNoteStore 相同）
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
