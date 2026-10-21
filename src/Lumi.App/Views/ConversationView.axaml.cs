using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 訊息流。code-behind 只放視覺行為：在最底部時跟著新內容自動捲動，使用者往上捲就不打擾。
/// <c>MessageScroller</c>、<c>RetryOtherModelButton</c> 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 直接使用。
/// </summary>
public partial class ConversationView : UserControl
{
    private const double StickToBottomTolerance = 24;

    private bool _stickToBottom = true;

    public ConversationView()
    {
        InitializeComponent();
        MessageScroller.ScrollChanged += OnMessageScrollChanged;
    }

    public void ScrollToEnd() => MessageScroller.ScrollToEnd();

    // 在最底部時跟著新內容捲動；使用者往上捲就不打擾，捲回底部再恢復
    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            if (_stickToBottom)
                MessageScroller.ScrollToEnd();
            return;
        }

        var distanceToBottom = MessageScroller.Extent.Height - MessageScroller.Viewport.Height - MessageScroller.Offset.Y;
        _stickToBottom = distanceToBottom <= StickToBottomTolerance;
    }
}
