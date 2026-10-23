namespace Lumi.Infrastructure.Startup;

/// <summary>啟動失敗的原因分類（M9 9C）。文字由 App 的資源檔負責，這裡只有分類。</summary>
public enum StartupFailureKind
{
    /// <summary>資料庫被其他程序鎖住（SQLITE_BUSY／SQLITE_LOCKED），常見於第二個實例。</summary>
    DatabaseLocked,

    /// <summary>沒有權限讀寫資料資料夾或資料庫（UnauthorizedAccessException、SQLITE_READONLY／SQLITE_PERM）。</summary>
    AccessDenied,

    /// <summary>磁碟空間不足（IOException 的 ERROR_DISK_FULL／ERROR_HANDLE_DISK_FULL，或 SQLITE_FULL）。</summary>
    DiskFull,

    /// <summary>資料庫是較新版本的 Lumi 建立的（<see cref="Persistence.DatabaseTooNewException"/>）。</summary>
    DatabaseTooNew,

    /// <summary>設定檔存在但讀不進來（<see cref="Settings.SettingsFileUnreadableException"/>）。</summary>
    SettingsUnreadable,

    /// <summary>其他。</summary>
    Other,
}
