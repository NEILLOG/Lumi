using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// schema 版本管理：依序執行的 migration，第 N 個元素把資料庫從 N 版升到 N+1 版。
/// 只能往後加新的 migration，已發布的不能改。
/// </summary>
internal static class SqliteSchema
{
    private static readonly string[] Migrations =
    [
        // 1：對話與訊息
        """
        CREATE TABLE conversations (
          id          TEXT PRIMARY KEY,
          title       TEXT NOT NULL,
          created_at  TEXT NOT NULL,
          updated_at  TEXT NOT NULL
        );
        CREATE INDEX ix_conversations_updated ON conversations(updated_at);

        CREATE TABLE messages (
          id              TEXT PRIMARY KEY,
          conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
          seq             INTEGER NOT NULL,
          role            TEXT NOT NULL,
          status          TEXT NOT NULL,
          model_id        TEXT,
          content_json    TEXT NOT NULL,
          input_tokens    INTEGER,
          output_tokens   INTEGER,
          cache_creation_input_tokens INTEGER,
          cache_read_input_tokens     INTEGER,
          created_at      TEXT NOT NULL,
          UNIQUE (conversation_id, seq)
        );
        """,

        // 2：訊息多了 role = 'tool'、內容多了 tool_use／tool_result 區塊（M3）。結構沒有變，
        // 只升版本號——讓 M3 之前的程式明確拒絕開啟，而不是讀到不認得的角色讀到一半壞掉。
        "SELECT 1;",

        // 3：對話的上下文項目（M4：使用者加入的檔案、資料夾、圖片）。同一對話裡相同種類與路徑只會有一列。
        // 之後的內容區塊（ContextBlock、ImageBlock）也在這個版本：舊版程式不認得，所以要擋。
        """
        CREATE TABLE conversation_context (
          id                     TEXT PRIMARY KEY,
          conversation_id        TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
          kind                   TEXT NOT NULL,
          path                   TEXT NOT NULL,
          display_name           TEXT NOT NULL,
          added_at               TEXT NOT NULL,
          embedded_in_message_id TEXT,
          UNIQUE (conversation_id, kind, path)
        );
        CREATE INDEX ix_conversation_context_conversation ON conversation_context(conversation_id, added_at);
        """,
    ];

    public static int LatestVersion => Migrations.Length;

    public static void Migrate(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        using var transaction = connection.BeginTransaction();
        var current = ReadVersion(connection, transaction);
        if (current > LatestVersion)
            throw new InvalidOperationException(
                $"資料庫是較新版本的 Lumi 建立的（schema {current}，這個版本只認得到 {LatestVersion}），為了避免寫壞資料，不開啟。");

        for (var version = current; version < LatestVersion; version++)
            Execute(connection, Migrations[version], transaction);

        if (current != LatestVersion)
        {
            Execute(connection, "DELETE FROM schema_version;", transaction);
            Execute(connection, $"INSERT INTO schema_version (version) VALUES ({LatestVersion});", transaction);
        }
        transaction.Commit();
    }

    public static int ReadVersion(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(version) FROM schema_version;";
        var result = command.ExecuteScalar();
        return result is long version ? (int)version : 0;
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
