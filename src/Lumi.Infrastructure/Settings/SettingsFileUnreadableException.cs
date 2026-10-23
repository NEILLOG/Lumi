namespace Lumi.Infrastructure.Settings;

/// <summary>
/// 設定檔存在但讀不進來（權限不足、被其他程式鎖住等 I/O 問題）。損毀的 JSON 不是這個例外：那種情況會改名保留並用預設值。
/// 啟動失敗說明（M9 9C）用型別辨識成「設定檔無法讀取」。
/// </summary>
public sealed class SettingsFileUnreadableException : IOException
{
    public SettingsFileUnreadableException(string path, Exception inner)
        : base($"無法讀取設定檔 {System.IO.Path.GetFileName(path)}。", inner)
    {
        FilePath = path;
    }

    /// <summary>設定檔完整路徑（只給說明與 log 用，不含內容）。</summary>
    public string FilePath { get; }
}
