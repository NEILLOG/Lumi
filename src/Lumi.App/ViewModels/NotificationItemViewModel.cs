using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Reminders;

namespace Lumi.App.ViewModels;

/// <summary>
/// 一則通知在畫面上的項目（工作台的通知列與小精靈的提醒氣泡共用）。只存在記憶體，按關閉才從清單移除。
/// 有 <see cref="AppNotification.Target"/>（排程執行的結果）時多一顆「查看」：打開紀錄，然後關掉這則通知。
/// </summary>
public sealed class NotificationItemViewModel
{
    private readonly Action _dismiss;
    private readonly Func<string, Task>? _openScheduledRun;

    /// <param name="openScheduledRun">打開一筆排程執行紀錄（帶 RunId）；沒有提供時「查看」什麼都不做。</param>
    public NotificationItemViewModel(AppNotification notification, Action dismiss, Func<string, Task>? openScheduledRun = null)
    {
        Notification = notification;
        _dismiss = dismiss;
        _openScheduledRun = openScheduledRun;
        DismissCommand = new RelayCommand(dismiss);
        ViewCommand = new AsyncRelayCommand(ViewAsync);
    }

    public AppNotification Notification { get; }

    public string Id => Notification.Id;

    public string Title => Notification.Title;

    public string Message => Notification.Message;

    public DateTimeOffset At => Notification.At;

    public NotificationKind Kind => Notification.Kind;

    /// <summary>錯過彙總：畫面上用較中性的樣式。</summary>
    public bool IsMissedSummary => Kind == NotificationKind.MissedSummary;

    /// <summary>排程跑 prompt 的結果：圖示用時鐘，不用鈴鐺。</summary>
    public bool IsScheduledRun => Kind == NotificationKind.ScheduledRun;

    /// <summary>有「查看」（通知帶著要打開的東西）。</summary>
    public bool HasView => Notification.Target is OpenScheduledRun;

    /// <summary>「查看」按鈕的文字與可存取名稱之外的 Tooltip，帶上標題。</summary>
    public string ViewTip => string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Notify_ViewTipFormat, Title);

    /// <summary>關閉鈕的 Tooltip 與可存取名稱（帶上標題，讀螢幕的人知道關的是哪一則）。</summary>
    public string DismissLabel => string.Format(CultureInfo.CurrentCulture, Strings.Notifications_DismissNamed, Title);

    public IRelayCommand DismissCommand { get; }

    /// <summary>「查看」：打開通知指向的紀錄，成功後關閉這則通知。</summary>
    public IAsyncRelayCommand ViewCommand { get; }

    private async Task ViewAsync()
    {
        if (Notification.Target is not OpenScheduledRun target || _openScheduledRun is null)
            return;
        await _openScheduledRun(target.RunId);
        _dismiss();
    }
}
