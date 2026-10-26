using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumi.App.ViewModels;

/// <summary>骨架：由包 20C 實作。設定頁「資料與隱私」。建構子簽章固定，DI 與 SettingsViewModel 依賴它。</summary>
public sealed partial class DataPrivacyViewModel : ObservableObject
{
    public DataPrivacyViewModel(IDataLocationProvider locations, IFolderOpener folders, IClipboardService clipboard)
    {
    }

    /// <summary>「怎麼控制」的連結按下時要求前往某個設定分類；SettingsViewModel 訂閱。</summary>
    public event Action<SettingsSection>? NavigateRequested;

    /// <summary>參數是 <see cref="SettingsSection"/> 的名稱（例如 "Memory"）。</summary>
    [RelayCommand]
    private void GoTo(string section)
    {
        if (Enum.TryParse<SettingsSection>(section, out var target))
            NavigateRequested?.Invoke(target);
    }
}
