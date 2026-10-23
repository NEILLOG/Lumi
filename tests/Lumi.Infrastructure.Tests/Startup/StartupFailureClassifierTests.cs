using System.Reflection;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Lumi.Infrastructure.Startup;
using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.Tests.Startup;

public class StartupFailureClassifierTests
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCantOpen = 14;
    private const int SqliteReadOnly = 8;
    private const int SqliteFull = 13;

    private static SqliteException Sqlite(int code) => new($"sqlite error {code}", code);

    [Theory]
    [InlineData(SqliteBusy)]
    [InlineData(SqliteLocked)]
    public void SqliteBusyOrLocked_IsDatabaseLocked(int code) =>
        Assert.Equal(StartupFailureKind.DatabaseLocked, StartupFailureClassifier.Classify(Sqlite(code)));

    [Fact]
    public void SqliteFull_IsDiskFull() =>
        Assert.Equal(StartupFailureKind.DiskFull, StartupFailureClassifier.Classify(Sqlite(SqliteFull)));

    [Theory]
    [InlineData(SqliteReadOnly)]
    public void SqliteReadOnly_IsAccessDenied(int code) =>
        Assert.Equal(StartupFailureKind.AccessDenied, StartupFailureClassifier.Classify(Sqlite(code)));

    [Fact]
    public void OtherSqliteErrors_AreOther() =>
        Assert.Equal(StartupFailureKind.Other, StartupFailureClassifier.Classify(Sqlite(SqliteCantOpen)));

    [Fact]
    public void UnauthorizedAccess_IsAccessDenied() =>
        Assert.Equal(StartupFailureKind.AccessDenied, StartupFailureClassifier.Classify(new UnauthorizedAccessException("拒絕存取")));

    [Theory]
    [InlineData(0x70)]          // ERROR_DISK_FULL
    [InlineData(0x27)]          // ERROR_HANDLE_DISK_FULL
    [InlineData(unchecked((int)0x80070070))]   // 同一個錯誤碼，HRESULT 形式
    [InlineData(unchecked((int)0x80070027))]
    public void IoExceptionWithDiskFullCode_IsDiskFull(int hresult) =>
        Assert.Equal(StartupFailureKind.DiskFull, StartupFailureClassifier.Classify(new IOException("磁碟已滿", hresult)));

    [Fact]
    public void OtherIoExceptions_AreOther() =>
        Assert.Equal(StartupFailureKind.Other, StartupFailureClassifier.Classify(new IOException("別的 I/O 錯誤", 0x2)));

    [Fact]
    public void DatabaseTooNew_IsDatabaseTooNew() =>
        Assert.Equal(StartupFailureKind.DatabaseTooNew, StartupFailureClassifier.Classify(new DatabaseTooNewException(4, 3)));

    [Fact]
    public void SettingsFileUnreadable_IsSettingsUnreadable() =>
        Assert.Equal(StartupFailureKind.SettingsUnreadable,
            StartupFailureClassifier.Classify(new SettingsFileUnreadableException("/x/settings.json", new IOException("locked"))));

    [Fact]
    public void UnknownExceptions_AreOther() =>
        Assert.Equal(StartupFailureKind.Other, StartupFailureClassifier.Classify(new InvalidOperationException("不知道")));

    [Fact]
    public void Wrapped_InnerExceptionsAreFound()
    {
        var wrapped = new InvalidOperationException("外層", new ApplicationException("中層", Sqlite(SqliteBusy)));

        Assert.Equal(StartupFailureKind.DatabaseLocked, StartupFailureClassifier.Classify(wrapped));
    }

    [Fact]
    public void AggregateExceptions_AreSearched()
    {
        var aggregate = new AggregateException(new TimeoutException("逾時"), Sqlite(SqliteBusy));

        Assert.Equal(StartupFailureKind.DatabaseLocked, StartupFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void OutermostClassifiableException_Wins()
    {
        var inner = new UnauthorizedAccessException("權限");
        var outer = new IOException("外層", inner);

        // 外層的 IOException 不是磁碟滿、不能分類，所以內層的權限不足勝出
        Assert.Equal(StartupFailureKind.AccessDenied, StartupFailureClassifier.Classify(outer));
    }

    [Fact]
    public void Classification_DoesNotDependOnMessageText()
    {
        var locked = new SqliteException("完全不同的文字", SqliteBusy);

        Assert.Equal(StartupFailureKind.DatabaseLocked, StartupFailureClassifier.Classify(locked));
    }

    [Fact]
    public void Every_kind_is_covered_by_a_test_above()
    {
        // 新增分類時提醒補測試
        var kinds = Enum.GetValues<StartupFailureKind>();
        Assert.Equal(6, kinds.Length);
    }
}
