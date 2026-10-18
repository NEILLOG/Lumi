using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>code-behind 只放視覺行為：Enter 送出、自動捲到底、啟動時載入。</summary>
public partial class WorkbenchWindow : Window
{
    private const double StickToBottomTolerance = 24;

    private readonly WorkbenchViewModel _viewModel;
    private bool _stickToBottom = true;

    public WorkbenchWindow(WorkbenchViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // TextBox 自己會處理 Enter，要在 tunnel 階段先攔下來
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        MessageScroller.ScrollChanged += OnMessageScrollChanged;
        // 選了模型就關掉彈出選單
        viewModel.ModelSwitcher.Selected += () => CapsuleButton.Flyout?.Hide();
        viewModel.RetrySwitcher.Selected += () => RetryOtherModelButton.Flyout?.Hide();
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
        MessageScroller.ScrollToEnd();
        InputBox.Focus();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        // 輸入法組字中的 Enter 不會走到這裡（由 IME 處理），所以不用另外判斷
        e.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null))
            _viewModel.SendCommand.Execute(null);
    }

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
