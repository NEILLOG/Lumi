using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Reminders;

namespace Lumi.App.ViewModels;

/// <summary>
/// 一則通知在畫面上的項目（工作台的通知列與小精靈的提醒氣泡共用）。只存在記憶體，按關閉才從清單移除。
/// </summary>
public sealed class NotificationItemViewModel
{
    public NotificationItemViewModel(AppNotification notification, Action dismiss)
    {
        Notification = notification;
        DismissCommand = new RelayCommand(dismiss);
    }

    public AppNotification Notification { get; }

    public string Id => Notification.Id;

    public string Title => Notification.Title;

    public string Message => Notification.Message;

    public DateTimeOffset At => Notification.At;

    public NotificationKind Kind => Notification.Kind;

    /// <summary>錯過彙總：畫面上用較中性的樣式。</summary>
    public bool IsMissedSummary => Kind == NotificationKind.MissedSummary;

    /// <summary>關閉鈕的 Tooltip 與可存取名稱（帶上標題，讀螢幕的人知道關的是哪一則）。</summary>
    public string DismissLabel => string.Format(CultureInfo.CurrentCulture, Strings.Notifications_DismissNamed, Title);

    public IRelayCommand DismissCommand { get; }
}
