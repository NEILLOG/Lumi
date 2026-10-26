using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Services;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>骨架：由包 20G 實作。設定首頁的狀態總覽。建構子與 <see cref="Attach"/> 的簽章固定，DI 與 SettingsViewModel 依賴它。</summary>
public sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    public HomeViewModel(IModelCatalog catalog, ConnectionStatusCache status, IUiDispatcher dispatcher)
    {
    }

    /// <summary>SettingsViewModel 建構時呼叫一次，把其他分類的 ViewModel 交給首頁讀取即時狀態。</summary>
    public void Attach(RemindersViewModel reminders, MemoryViewModel memory, ToolsViewModel tools, GeneralViewModel general, SpriteSettingsViewModel sprite)
    {
    }

    public void Dispose()
    {
    }
}
