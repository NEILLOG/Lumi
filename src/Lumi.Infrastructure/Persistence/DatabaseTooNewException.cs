namespace Lumi.Infrastructure.Persistence;

/// <summary>
/// 資料庫是較新版本的 Lumi 建立的（schema 版本比這個程式認得的還新），為了不寫壞資料而拒絕開啟。
/// 繼承 <see cref="InvalidOperationException"/>，既有的呼叫端不用改；啟動失敗說明（M9 9C）用型別辨識。
/// </summary>
public sealed class DatabaseTooNewException : InvalidOperationException
{
    public DatabaseTooNewException(int databaseVersion, int latestVersion)
        : base($"資料庫是較新版本的 Lumi 建立的（schema {databaseVersion}，這個版本只認得到 {latestVersion}），為了避免寫壞資料，不開啟。")
    {
        DatabaseVersion = databaseVersion;
        LatestVersion = latestVersion;
    }

    public int DatabaseVersion { get; }

    public int LatestVersion { get; }
}
