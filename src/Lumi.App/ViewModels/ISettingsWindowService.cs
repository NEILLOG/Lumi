namespace Lumi.App.ViewModels;

public enum SettingsSection { Connections, Models, General }

/// <param name="ConnectionId">要選取的連線；null 表示不指定。</param>
/// <param name="FocusApiKey">直接聚焦金鑰輸入框（缺金鑰流程）。</param>
/// <param name="Notice">顯示在連線表單上方的一行說明。</param>
public sealed record SettingsNavigation(
    SettingsSection Section, string? ConnectionId = null, bool FocusApiKey = false, string? Notice = null);

/// <summary>開啟設定視窗（單一執行個體，已開啟時只帶到前面並切換位置）。</summary>
public interface ISettingsWindowService
{
    /// <returns>視窗關閉時完成。</returns>
    Task ShowAsync(SettingsNavigation navigation);
}
