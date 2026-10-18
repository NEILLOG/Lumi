namespace Lumi.App.ViewModels;

/// <summary>跳出確認視窗（刪除、放棄變更）。ViewModel 測試用假的。</summary>
public interface IConfirmService
{
    /// <returns>使用者按了確認鍵回 true；取消或關閉視窗回 false。</returns>
    Task<bool> ConfirmAsync(string message, string confirmText);
}
