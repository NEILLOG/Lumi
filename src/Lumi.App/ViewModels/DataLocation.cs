namespace Lumi.App.ViewModels;

/// <summary>「資料與隱私」頁的一個存放位置。<paramref name="Path"/> 是實際的完整路徑（檔案或資料夾）；鑰匙圈這類沒有路徑的項目是 null。</summary>
/// <param name="Id">穩定的識別字（測試與 AutomationId 用）：data、database、attachments、settings、secrets、logs、browser、sprites、notes。</param>
/// <param name="IsFile">Path 是檔案；「顯示位置」時開啟它所在的資料夾。</param>
public sealed record DataLocation(string Id, string Name, string Description, string? Path, bool IsFile);

/// <summary>依實際的設定回傳 Lumi 存放使用者資料的位置，順序就是設定頁的顯示順序。</summary>
public interface IDataLocationProvider
{
    IReadOnlyList<DataLocation> GetLocations();
}
