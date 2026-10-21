namespace Lumi.App.ViewModels;

/// <summary>在系統的檔案管理員（Explorer、Finder）開啟資料夾；資料夾不存在時先建立。ViewModel 不碰實作，測試用假的。</summary>
public interface IFolderOpener
{
    /// <returns>成功開啟回 true。</returns>
    Task<bool> OpenAsync(string directory);
}
