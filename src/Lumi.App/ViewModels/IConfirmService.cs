namespace Lumi.App.ViewModels;

/// <summary>跳出確認視窗（刪除、放棄變更）。ViewModel 測試用假的。</summary>
public interface IConfirmService
{
    /// <returns>使用者按了確認鍵回 true；取消或關閉視窗回 false。</returns>
    Task<bool> ConfirmAsync(string message, string confirmText);

    /// <summary>
    /// 破壞性動作的確認（刪除、清除、放棄變更）：確認鍵用 danger 風格（紅框紅字），不是 primary。
    /// 預設轉給 <see cref="ConfirmAsync"/>，所以只在乎有沒有確認的實作（測試用的假服務）不用改。
    /// </summary>
    Task<bool> ConfirmDestructiveAsync(string message, string confirmText) => ConfirmAsync(message, confirmText);
}
