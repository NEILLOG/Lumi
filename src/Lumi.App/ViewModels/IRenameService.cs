namespace Lumi.App.ViewModels;

/// <summary>重新命名對話的輸入框（工作台「⋯」›「重新命名對話」）。</summary>
public interface IRenameService
{
    /// <summary>
    /// 顯示預填目前標題的輸入框。按儲存回傳輸入的文字（原樣，清理與長度上限由呼叫端套用）；取消或關閉回 null。
    /// </summary>
    Task<string?> PromptAsync(string currentTitle, int maxLength);
}
