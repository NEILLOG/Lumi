using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Core.Reminders;

namespace Lumi.App.ViewModels;

/// <summary>骨架：由包 20B 實作。設定頁「提醒排程」。建構子簽章固定，DI 與 SettingsViewModel 依賴它。</summary>
public sealed partial class RemindersViewModel : ObservableObject, IDisposable
{
    public RemindersViewModel(ReminderService reminders, IConfirmService confirm, IUiDispatcher dispatcher, TimeProvider time)
    {
    }

    public void Dispose()
    {
    }
}
