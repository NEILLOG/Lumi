namespace Lumi.App.ViewModels;

/// <summary>寫入系統剪貼簿。ViewModel 不碰 Avalonia，測試用假的。</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}
