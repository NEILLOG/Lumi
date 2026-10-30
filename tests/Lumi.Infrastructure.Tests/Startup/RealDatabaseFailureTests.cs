using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Startup;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests.Startup;

/// <summary>
/// 不用假的例外：用真的 SQLite 檔案製造失敗，確認分類器拿到的錯誤碼與實際情況一致（M9 9C）。
/// </summary>
public class RealDatabaseFailureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-realdb-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task DatabaseHeldExclusivelyByAnotherConnection_IsClassifiedAsDatabaseLocked()
    {
        // 第二個程序的情形：另一條連線獨占資料庫，這邊開啟時拿到的是真的 SQLITE_BUSY／LOCKED
        Directory.CreateDirectory(_directory);
        await using var holder = new SqliteConnection($"Data Source={DatabasePath}");
        await holder.OpenAsync();
        await using (var pragma = holder.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA locking_mode=EXCLUSIVE; CREATE TABLE holder (x INTEGER); BEGIN EXCLUSIVE;";
            await pragma.ExecuteNonQueryAsync();
        }

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => NewStore().GetLatestAsync());

        Assert.Equal(StartupFailureKind.DatabaseLocked, StartupFailureClassifier.Classify(ex));
        Assert.IsType<SqliteException>(FindSqlite(ex));
    }

    [Fact]
    public async Task DatabaseFromANewerLumi_IsClassifiedAsDatabaseTooNew()
    {
        var store = NewStore();
        await store.CreateAsync("建立");
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS request_usage; DROP TABLE IF EXISTS usage_hints; UPDATE schema_version SET version = {SqliteSchema.LatestVersion + 1};";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var ex = await Assert.ThrowsAsync<DatabaseTooNewException>(() => NewStore().GetLatestAsync());

        Assert.Equal(StartupFailureKind.DatabaseTooNew, StartupFailureClassifier.Classify(ex));
    }

    private static Exception? FindSqlite(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqliteException)
                return current;
        }
        return null;
    }
}
