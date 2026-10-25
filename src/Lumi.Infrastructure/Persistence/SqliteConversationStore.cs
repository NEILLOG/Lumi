using System.Globalization;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IConversationStore"/> 的 SQLite 實作。每次操作開一條連線（有連線池，很便宜）；
/// 第一次使用時建立資料夾並把 schema 補到最新版。訊息的順序以 <c>seq</c> 為準，不靠 id。
/// </summary>
public sealed class SqliteConversationStore : IConversationStore, IConversationContextStore, IAttachmentReferences
{
    private readonly string _connectionString;
    private readonly Lazy<Task> _initialization;

    public SqliteConversationStore(IOptions<PersistenceOptions> options)
    {
        var path = options.Value.ResolveDatabasePath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
        _initialization = new Lazy<Task>(() => Task.Run(() =>
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            SqliteSchema.Migrate(connection);
        }));
    }

    public async Task<Conversation> CreateAsync(string title, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(Conversation.NewId(), title, now, now);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, $title, $created, $updated);";
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$created", FormatTime(now));
        command.Parameters.AddWithValue("$updated", FormatTime(now));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return conversation;
    }

    public async Task<Conversation?> GetLatestAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, created_at, updated_at FROM conversations ORDER BY updated_at DESC, rowid DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new Conversation(reader.GetString(0), reader.GetString(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)));
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, CancellationToken ct = default) => ListAsync(limit, null, ct);

    public async Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 游標條件與 ORDER BY 一致：更新時間較早，或更新時間相同而 id 較小。相同 updated_at 的對話靠 id 分出先後，分頁不重複也不漏
        var cursor = before is null
            ? ""
            : "AND (c.updated_at < $beforeUpdated OR (c.updated_at = $beforeUpdated AND c.id < $beforeId))";
        command.CommandText = $"""
            SELECT c.id, c.title, c.created_at, c.updated_at FROM conversations c
            WHERE EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = c.id)
            {cursor}
            ORDER BY c.updated_at DESC, c.id DESC LIMIT $limit;
            """;
        if (before is not null)
        {
            command.Parameters.AddWithValue("$beforeUpdated", FormatTime(before.UpdatedAt));
            command.Parameters.AddWithValue("$beforeId", before.Id);
        }
        command.Parameters.AddWithValue("$limit", limit);

        var list = new List<Conversation>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new Conversation(reader.GetString(0), reader.GetString(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3))));
        return list;
    }

    public async Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 訊息與上下文項目靠外鍵 ON DELETE CASCADE 一併刪除（OpenAsync 已開啟外鍵）
        command.CommandText = "DELETE FROM conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", conversationId);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        var text = query.Trim();
        if (text.Length == 0 || limit <= 0)
            return [];

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // LIKE 的 % 與 _ 以及跳脫字元本身都先跳脫，ESCAPE '\' 指定跳脫字元。LIKE 對 ASCII 不分大小寫；
        // 中文沒有大小寫，1～2 字也能命中（不像 FTS trigram 要求至少 3 個字元）。
        // 片段取第一則命中的訊息（依送出順序）；只有標題命中時 hit 是 NULL。
        command.CommandText = """
            SELECT c.id, c.title, c.created_at, c.updated_at,
              (SELECT m.search_text FROM messages m
               WHERE m.conversation_id = c.id AND m.search_text LIKE $pattern ESCAPE '\'
               ORDER BY m.seq LIMIT 1) AS hit
            FROM conversations c
            WHERE EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = c.id)
              AND (c.title LIKE $pattern ESCAPE '\'
                   OR EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = c.id AND m.search_text LIKE $pattern ESCAPE '\'))
            ORDER BY c.updated_at DESC, c.id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", "%" + EscapeLike(text) + "%");
        command.Parameters.AddWithValue("$limit", limit);

        var hits = new List<ConversationSearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var conversation = new Conversation(reader.GetString(0), reader.GetString(1), ParseTime(reader.GetString(2)), ParseTime(reader.GetString(3)));
            var snippet = reader.IsDBNull(4) ? string.Empty : SearchSnippet.Create(reader.GetString(4), text);
            hits.Add(new ConversationSearchHit(conversation, snippet));
        }
        return hits;
    }

    private static string EscapeLike(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM messages WHERE id = (
              SELECT id FROM messages WHERE conversation_id = $conversation ORDER BY seq DESC LIMIT 1);
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, role, status, model_id, content_json,
                   input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, created_at, turn_suffix
            FROM messages WHERE conversation_id = $conversation ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);

        var messages = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var content = JsonSerializer.Deserialize<List<ContentBlock>>(reader.GetString(4))
                          ?? throw new InvalidDataException($"訊息 {reader.GetString(0)} 的內容無法讀取。");
            TokenUsage? usage = reader.IsDBNull(5)
                ? null
                : new TokenUsage(reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt32(8));
            messages.Add(new ChatMessage(
                Id: reader.GetString(0),
                Role: ParseRole(reader.GetString(1)),
                Content: content,
                CreatedAt: ParseTime(reader.GetString(9)),
                Status: ParseStatus(reader.GetString(2)),
                ModelId: reader.IsDBNull(3) ? null : reader.GetString(3),
                Usage: usage,
                TurnSuffix: reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return messages;
    }

    public async Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO messages (id, conversation_id, seq, role, status, model_id, content_json,
                                      input_tokens, output_tokens, cache_creation_input_tokens, cache_read_input_tokens, created_at, search_text, turn_suffix)
                SELECT $id, $conversation, COALESCE(MAX(seq), 0) + 1, $role, $status, $model, $content,
                       $input, $output, $cacheCreation, $cacheRead, $created, $search, $suffix
                FROM messages WHERE conversation_id = $conversation;
                """;
            insert.Parameters.AddWithValue("$id", message.Id);
            insert.Parameters.AddWithValue("$conversation", conversationId);
            insert.Parameters.AddWithValue("$role", FormatRole(message.Role));
            insert.Parameters.AddWithValue("$status", FormatStatus(message.Status));
            insert.Parameters.AddWithValue("$model", (object?)message.ModelId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$content", JsonSerializer.Serialize<IReadOnlyList<ContentBlock>>(message.Content));
            insert.Parameters.AddWithValue("$input", (object?)message.Usage?.InputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$output", (object?)message.Usage?.OutputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cacheCreation", (object?)message.Usage?.CacheCreationInputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cacheRead", (object?)message.Usage?.CacheReadInputTokens ?? DBNull.Value);
            insert.Parameters.AddWithValue("$created", FormatTime(message.CreatedAt));
            insert.Parameters.AddWithValue("$search", SearchTextExtractor.Extract(message.Content));
            insert.Parameters.AddWithValue("$suffix", (object?)message.TurnSuffix ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE conversations SET updated_at = $updated WHERE id = $id;";
            touch.Parameters.AddWithValue("$updated", FormatTime(DateTimeOffset.UtcNow));
            touch.Parameters.AddWithValue("$id", conversationId);
            await touch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetSystemPromptAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT system_prompt FROM conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", conversationId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task SetSystemPromptAsync(string conversationId, string systemPrompt, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET system_prompt = $system WHERE id = $id;";
        command.Parameters.AddWithValue("$system", systemPrompt);
        command.Parameters.AddWithValue("$id", conversationId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<string?> GetToolSnapshotAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tool_snapshot FROM conversations WHERE id = $id;";
        command.Parameters.AddWithValue("$id", conversationId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
    }

    public async Task SetToolSnapshotAsync(string conversationId, string snapshotJson, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET tool_snapshot = $snapshot WHERE id = $id;";
        command.Parameters.AddWithValue("$snapshot", snapshotJson);
        command.Parameters.AddWithValue("$id", conversationId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetTurnSuffixAsync(string conversationId, string messageId, string? turnSuffix, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET turn_suffix = $suffix WHERE id = $id AND conversation_id = $conversation;";
        command.Parameters.AddWithValue("$suffix", (object?)turnSuffix ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", messageId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string conversationId, string title, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conversations SET title = $title WHERE id = $id;";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", conversationId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // ---- 附件引用（IAttachmentReferences） ----

    public async Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // 只有含圖片區塊的訊息才需要解析（序列化格式固定是 "type":"image"）
        command.CommandText = "SELECT content_json FROM messages WHERE content_json LIKE '%\"type\":\"image\"%';";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var blocks = JsonSerializer.Deserialize<List<ContentBlock>>(reader.GetString(0)) ?? [];
            foreach (var image in blocks.OfType<ImageBlock>())
                names.Add(image.FileName);
        }
        return names;
    }

    // ---- 上下文項目（IConversationContextStore） ----

    public async Task<IReadOnlyList<ContextItem>> GetAsync(string conversationId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, path, display_name, added_at, embedded_in_message_id
            FROM conversation_context WHERE conversation_id = $conversation ORDER BY added_at, rowid;
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var items = new List<ContextItem>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            items.Add(ReadItem(reader));
        return items;
    }

    public async Task<ContextItem> AddAsync(string conversationId, ContextKind kind, string path, string displayName, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using (var update = connection.CreateCommand())
        {
            // 已經存在：已送出的重新變成「待送出」（再加入同一個檔案＝再拍一次新快照）；還沒送出的維持原樣
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE conversation_context SET embedded_in_message_id = NULL, added_at = $added, display_name = $name
                WHERE conversation_id = $conversation AND kind = $kind AND path = $path AND embedded_in_message_id IS NOT NULL;
                """;
            update.Parameters.AddWithValue("$added", FormatTime(now));
            update.Parameters.AddWithValue("$name", displayName);
            update.Parameters.AddWithValue("$conversation", conversationId);
            update.Parameters.AddWithValue("$kind", FormatKind(kind));
            update.Parameters.AddWithValue("$path", path);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO conversation_context (id, conversation_id, kind, path, display_name, added_at, embedded_in_message_id)
                VALUES ($id, $conversation, $kind, $path, $name, $added, NULL);
                """;
            insert.Parameters.AddWithValue("$id", ContextItem.NewId());
            insert.Parameters.AddWithValue("$conversation", conversationId);
            insert.Parameters.AddWithValue("$kind", FormatKind(kind));
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$name", displayName);
            insert.Parameters.AddWithValue("$added", FormatTime(now));
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        ContextItem item;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, kind, path, display_name, added_at, embedded_in_message_id
                FROM conversation_context WHERE conversation_id = $conversation AND kind = $kind AND path = $path;
                """;
            select.Parameters.AddWithValue("$conversation", conversationId);
            select.Parameters.AddWithValue("$kind", FormatKind(kind));
            select.Parameters.AddWithValue("$path", path);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            item = ReadItem(reader);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return item;
    }

    public async Task RemoveAsync(string conversationId, string itemId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM conversation_context WHERE conversation_id = $conversation AND id = $id;";
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$id", itemId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkEmbeddedAsync(string conversationId, IReadOnlyCollection<string> itemIds, string messageId, CancellationToken ct = default)
    {
        if (itemIds.Count == 0)
            return;

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var id in itemIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE conversation_context SET embedded_in_message_id = $message WHERE conversation_id = $conversation AND id = $id;";
            command.Parameters.AddWithValue("$message", messageId);
            command.Parameters.AddWithValue("$conversation", conversationId);
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static ContextItem ReadItem(SqliteDataReader reader) => new(
        Id: reader.GetString(0),
        Kind: ParseKind(reader.GetString(1)),
        Path: reader.GetString(2),
        DisplayName: reader.GetString(3),
        AddedAt: ParseTime(reader.GetString(4)),
        EmbeddedInMessageId: reader.IsDBNull(5) ? null : reader.GetString(5));

    private static string FormatKind(ContextKind kind) => kind switch
    {
        ContextKind.File => "file",
        ContextKind.Folder => "folder",
        ContextKind.Image => "image",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ContextKind ParseKind(string text) => text switch
    {
        "file" => ContextKind.File,
        "folder" => ContextKind.Folder,
        "image" => ContextKind.Image,
        _ => throw new InvalidDataException($"未知的上下文種類：{text}"),
    };

    // internal：SearchTextBackfill 與測試要用同一套初始化（先跑 migration）與外鍵設定
    internal async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await _initialization.Value.WaitAsync(ct).ConfigureAwait(false);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    // 一律存 UTC、固定格式，字串排序就等於時間排序
    private static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string FormatRole(ChatRole role) => role switch
    {
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static ChatRole ParseRole(string text) => text switch
    {
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => throw new InvalidDataException($"未知的角色：{text}"),
    };

    private static string FormatStatus(MessageStatus status) => status switch
    {
        MessageStatus.Complete => "complete",
        MessageStatus.Stopped => "stopped",
        MessageStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static MessageStatus ParseStatus(string text) => text switch
    {
        "complete" => MessageStatus.Complete,
        "stopped" => MessageStatus.Stopped,
        "failed" => MessageStatus.Failed,
        _ => throw new InvalidDataException($"未知的狀態：{text}"),
    };
}
