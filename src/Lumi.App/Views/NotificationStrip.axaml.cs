using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>工作台頂端的提醒通知列（ui-spec 第 13 節）。沒有 code-behind 行為，全部由 <c>NotificationStripViewModel</c> 決定。</summary>
public partial class NotificationStrip : UserControl
{
    public NotificationStrip()
    {
        InitializeComponent();
    }
}
