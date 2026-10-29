namespace Lumi.App.ViewModels;

/// <summary>用系統預設的瀏覽器開啟網址。只接受 http／https。ViewModel 不碰實作，測試用假的。</summary>
public interface IUrlLauncher
{
    /// <returns>成功開啟回 true。</returns>
    Task<bool> OpenAsync(Uri url);
}
