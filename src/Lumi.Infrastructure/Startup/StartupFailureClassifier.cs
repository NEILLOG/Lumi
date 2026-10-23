using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace Lumi.Infrastructure.Startup;

/// <summary>
/// 把啟動時的例外分成說明用的原因（M9 9C）。純函式：沿著 InnerException 與 AggregateException 往下找，
/// 從最外層開始，第一個能分類的就是結果。不讀訊息文字（只看型別與錯誤碼），所以不會因為訊息內容改變而失準，
/// 也不會把訊息（可能含敏感資訊）帶進分類結果。
/// </summary>
public static class StartupFailureClassifier
{
    // SQLite 主要錯誤碼（SqliteException.SqliteErrorCode，不含擴充碼）
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqlitePerm = 3;
    private const int SqliteReadOnly = 8;
    private const int SqliteFull = 13;

    // Windows 的 ERROR_DISK_FULL（112 = 0x70）與 ERROR_HANDLE_DISK_FULL（39 = 0x27），取 HResult 的低 16 位
    private const int ErrorDiskFull = 0x70;
    private const int ErrorHandleDiskFull = 0x27;

    public static StartupFailureKind Classify(Exception exception)
    {
        foreach (var ex in Chain(exception))
        {
            var kind = ClassifyOne(ex);
            if (kind is not StartupFailureKind.Other)
                return kind;
        }
        return StartupFailureKind.Other;
    }

    private static StartupFailureKind ClassifyOne(Exception ex) => ex switch
    {
        DatabaseTooNewException => StartupFailureKind.DatabaseTooNew,
        SettingsFileUnreadableException => StartupFailureKind.SettingsUnreadable,
        SqliteException sqlite => ClassifySqlite(sqlite.SqliteErrorCode),
        UnauthorizedAccessException => StartupFailureKind.AccessDenied,
        IOException io when IsDiskFull(io) => StartupFailureKind.DiskFull,
        _ => StartupFailureKind.Other,
    };

    private static StartupFailureKind ClassifySqlite(int code) => code switch
    {
        SqliteBusy or SqliteLocked => StartupFailureKind.DatabaseLocked,
        SqliteFull => StartupFailureKind.DiskFull,
        SqlitePerm or SqliteReadOnly => StartupFailureKind.AccessDenied,
        _ => StartupFailureKind.Other,
    };

    private static bool IsDiskFull(IOException io)
    {
        var code = io.HResult & 0xFFFF;
        return code is ErrorDiskFull or ErrorHandleDiskFull;
    }

    /// <summary>外層到內層的例外鏈（包含 AggregateException 的每一個內層）。</summary>
    private static IEnumerable<Exception> Chain(Exception exception)
    {
        var queue = new Queue<Exception>([exception]);
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current))
                continue;

            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    queue.Enqueue(inner);
            }
            if (current.InnerException is { } next)
                queue.Enqueue(next);
        }
    }
}
