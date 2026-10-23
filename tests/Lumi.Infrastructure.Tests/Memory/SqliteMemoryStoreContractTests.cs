using Lumi.Core.Memory;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests.Memory;

/// <summary>SQLite 版跑同一組契約測試（每個測試一個暫存資料庫）。</summary>
public sealed class SqliteMemoryStoreContractTests : MemoryStoreContractTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-memory-contract-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    protected override IMemoryStore CreateStore(MemoryOptions options, MemoryTestClock clock) => new SqliteMemoryStore(
        new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath })),
        options,
        clock,
        NullLogger<SqliteMemoryStore>.Instance);

    public override void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
