using System.Collections.ObjectModel;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 提醒通知的送達（<see cref="INotificationSink"/> 的 App 實作，M10 10E）。
/// 通知只存在記憶體，<b>不自動消失</b>，使用者按關閉才收起；最多保留 <see cref="MaxItems"/> 則，超過丟掉最舊的。
/// 工作台的通知列與小精靈的提醒氣泡都綁定 <see cref="Items"/>，同一則通知在兩邊各顯示一次（各自只顯示其中一部分）。
/// 加入時若工作台與小精靈都沒有顯示，要求顯示小精靈（走協調者既有的 <see cref="IAppModeCoordinator.ShowSprite"/>）。
/// 工作台與小精靈的視窗與協調者透過委派延遲取得，避免建構時的循環相依（工具 → 提醒服務 → 這裡 → 協調者 → 對話 → 工具）。
/// </summary>
public sealed class NotificationCenter : INotificationSink
{
    public const int MaxItems = 20;

    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IWindowHost> _windows;
    private readonly Func<IAppModeCoordinator> _modes;
    private readonly ILogger<NotificationCenter> _logger;

    public NotificationCenter(IUiDispatcher dispatcher, Func<IWindowHost> windows, Func<IAppModeCoordinator> modes, ILogger<NotificationCenter> logger)
    {
        _dispatcher = dispatcher;
        _windows = windows;
        _modes = modes;
        _logger = logger;
    }

    /// <summary>目前未確認的通知，最新的在最前面。只能在 UI 執行緒上讀寫。</summary>
    public ObservableCollection<NotificationItemViewModel> Items { get; } = new();

    /// <summary>送達：丟回 UI 執行緒加入清單（呼叫端可能在背景執行緒）。</summary>
    public Task ShowAsync(AppNotification notification, CancellationToken ct)
    {
        _dispatcher.Post(() => Add(notification));
        return Task.CompletedTask;
    }

    public void Dismiss(string id)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item is not null)
            Items.Remove(item);
    }

    public void DismissAll() => Items.Clear();

    private void Add(AppNotification notification)
    {
        try
        {
            // 同一則不重複加入（例如重送、或同一個 Id 的彙總）
            if (Items.Any(i => i.Id == notification.Id))
                return;

            Items.Insert(0, new NotificationItemViewModel(notification, () => Dismiss(notification.Id)));
            while (Items.Count > MaxItems)
                Items.RemoveAt(Items.Count - 1);

            // 兩個視窗都看不到：要求顯示小精靈（小精靈暫時隱藏時會提前結束隱藏）。
            // 只在作用中模式是小精靈時才這麼做：工作台模式下關掉工作台就是結束程式，不會有「工作台模式但沒有視窗」的正常狀態；
            // 唯一的例外是啟動時視窗還沒顯示（Start() 之前），這時 ShowSprite() 會把模式切成小精靈，之後 Start() 又開工作台，變成兩個視窗同時開
            var windows = _windows();
            var modes = _modes();
            if (!windows.IsWorkbenchVisible && !windows.IsSpriteVisible && modes.ActiveMode == ChatMode.Sprite)
                modes.ShowSprite();
        }
        catch (Exception e)
        {
            // 這裡是 UI 執行緒的 post 回呼，例外不會有人接，所以在這裡記下來
            _logger.LogError(e, "顯示提醒通知失敗");
        }
    }
}
