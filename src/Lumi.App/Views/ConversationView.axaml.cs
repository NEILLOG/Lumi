using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lumi.App.Views;

/// <summary>
/// 訊息流。code-behind 只放視覺行為：在最底部時跟著新內容自動捲動，使用者往上捲就不打擾；
/// 離開底部時顯示「回到最新訊息」（FT-01）；Windows 上可以按住中鍵拖曳捲動（FT-02）。
/// <c>MessageScroller</c>、<c>RetryOtherModelButton</c> 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 直接使用。
/// </summary>
public partial class ConversationView : UserControl
{
    private const double StickToBottomTolerance = 24;

    private bool _stickToBottom = true;

    public ConversationView()
    {
        InitializeComponent();
        AutoScroll = new MiddleButtonAutoScroll(this, MessageScroller, () => MiddleButtonScrollEnabled);
        MessageScroller.ScrollChanged += OnMessageScrollChanged;
        JumpToBottomButton.Click += (_, _) => JumpToBottom();
        // 要收得到子元素冒泡上來的 GotFocus，所以用 AddHandler
        MessageScroller.AddHandler(GotFocusEvent, OnMessageGotFocus);
    }

    /// <summary>按住中鍵拖曳捲動只在 Windows 啟用（macOS 沒有這個習慣）；測試可以打開。</summary>
    internal bool MiddleButtonScrollEnabled { get; set; } = OperatingSystem.IsWindows();

    internal MiddleButtonAutoScroll AutoScroll { get; }

    public void ScrollToEnd() => MessageScroller.ScrollToEnd();

    private double DistanceToBottom => MessageScroller.Extent.Height - MessageScroller.Viewport.Height - MessageScroller.Offset.Y;

    private void JumpToBottom()
    {
        _stickToBottom = true;
        MessageScroller.ScrollToEnd();
        UpdateJumpToBottom();
    }

    // 往上捲離開底部才出現；內容變短、捲回底部就消失
    private void UpdateJumpToBottom() =>
        JumpToBottomHost.IsVisible = !_stickToBottom && DistanceToBottom > StickToBottomTolerance;

    // MessageScroller 已關閉焦點時自動捲動（避免視窗重新啟用、滑鼠點擊就跳到可選取的文字）；
    // 只有鍵盤（Tab、方向鍵）移動焦點時，才把取得焦點的控制項捲進可視範圍
    private static void OnMessageGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional)
            (e.Source as Control)?.BringIntoView();
    }

    // 在最底部時跟著新內容捲動；使用者往上捲就不打擾，捲回底部再恢復
    private void OnMessageScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
        {
            if (_stickToBottom)
                MessageScroller.ScrollToEnd();
            else if (DistanceToBottom <= StickToBottomTolerance)
                _stickToBottom = true;   // 內容變短（例如換了對話）而已經在底部：恢復跟著捲
            UpdateJumpToBottom();
            return;
        }

        _stickToBottom = DistanceToBottom <= StickToBottomTolerance;
        UpdateJumpToBottom();
    }
}
