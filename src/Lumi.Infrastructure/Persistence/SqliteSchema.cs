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

        // 4：搜尋用的文字（M9 9A）。messages.search_text 是訊息的文字區塊（SearchTextExtractor），只給搜尋用；
        // 舊訊息是 '' ，由 SearchTextBackfill 在啟動後補上。app_meta 是小的 key-value 表，目前只記「已補完」旗標。
        """
        ALTER TABLE messages ADD COLUMN search_text TEXT NOT NULL DEFAULT '';

        CREATE TABLE app_meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        """,

        // 5：提醒與排程（M10 10D）。時間欄位一律存 UTC（固定格式，字串排序就是時間排序）；
        // trigger_json／action_json 是多型 JSON（判別欄位 type），欄位名稱與舊版 schedules.json 相同。
        // 列舉存數字（與舊版相同）；created_by 存 "Agent"／"User"。
        """
        CREATE TABLE scheduled_items (
          id            TEXT PRIMARY KEY,
          kind          INTEGER NOT NULL,
          title         TEXT NOT NULL,
          message       TEXT NOT NULL,
          trigger_json  TEXT NOT NULL,
          time_zone_id  TEXT NOT NULL,
          action_json   TEXT NOT NULL,
          status        INTEGER NOT NULL,
          enabled       INTEGER NOT NULL,
          next_fire_at  TEXT,
          last_fire_at  TEXT,
          created_by    TEXT NOT NULL,
          created_at    TEXT NOT NULL
        );
        CREATE INDEX ix_scheduled_items_due ON scheduled_items(status, enabled, next_fire_at);
        """,

        // 6：長期記憶（M14 14A）。id 用 AUTOINCREMENT，刪除後不重用（顯示與工具參數用 m<id>）。
        // source_conversation_id 只供參考，刻意不設外鍵：刪除對話不刪記憶。時間存 UTC 固定格式，到期日存 yyyy-MM-dd。
        """
        CREATE TABLE memories (
          id                     INTEGER PRIMARY KEY AUTOINCREMENT,
          content                TEXT NOT NULL,
          category               INTEGER NOT NULL,
          source                 INTEGER NOT NULL,
          source_conversation_id TEXT,
          created_at             TEXT NOT NULL,
          updated_at             TEXT NOT NULL,
          expires_at             TEXT
        );
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
            throw new DatabaseTooNewException(current, LatestVersion);

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
