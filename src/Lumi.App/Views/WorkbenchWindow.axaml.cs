using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Services;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lumi.App.ViewModels;
using Lumi.App.Views.Responsive;

namespace Lumi.App.Views;

/// <summary>code-behind 只放視覺行為：Enter 送出、自動捲到底、啟動時載入。</summary>
public partial class WorkbenchWindow : Window
{
    private const double StickToBottomTolerance = 24;

    private readonly WorkbenchViewModel _viewModel;
    private bool _stickToBottom = true;

    public WorkbenchWindow(WorkbenchViewModel viewModel, IAppModeCoordinator? modes = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ResponsiveClasses.Track(this);

        // TextBox 自己會處理 Enter，要在 tunnel 階段先攔下來
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        MessageScroller.ScrollChanged += OnMessageScrollChanged;
        // 拖放檔案／資料夾加入上下文；Ctrl／Cmd+V 貼上剪貼簿裡的截圖
        InputArea.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        InputArea.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        InputArea.AddHandler(DragDrop.DropEvent, OnDrop);
        // 選了模型就關掉彈出選單
        viewModel.ModelSwitcher.Selected += () => CapsuleButton.Flyout?.Hide();
        viewModel.RetrySwitcher.Selected += () => RetryOtherModelButton.Flyout?.Hide();
        Opened += OnOpened;

        // 工作台模式下關閉視窗＝結束程式；小精靈模式下只是關掉工作台（改成隱藏，之後還能從小精靈或托盤再開）
        Closing += (_, e) =>
        {
            if (modes is null || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
                return;
            if (!modes.WorkbenchMayClose())
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>捲到某一張工具卡片（小精靈的「在工作台檢視」、「開啟完整內容」）。視窗剛開啟時要等排版完成。</summary>
    public void ScrollToCall(string callId) => Dispatcher.UIThread.Post(() =>
    {
        var card = this.GetVisualDescendants().OfType<ToolCardView>()
            .FirstOrDefault(view => view.DataContext is ToolCardViewModel model && model.CallId == callId);
        card?.BringIntoView();
    }, DispatcherPriority.Loaded);

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
        MessageScroller.ScrollToEnd();
        InputBox.Focus();
    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            // 剪貼簿有圖片就加進上下文；沒有就照一般貼上文字
            e.Handled = true;
            if (!await _viewModel.Context.PasteImageAsync())
                InputBox.Paste();
            return;
        }

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

    // ---- 拖放 ----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accept = _viewModel.Context.CanChange && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        InputArea.Classes.Set("dropping", accept);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => InputArea.Classes.Set("dropping", false);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        InputArea.Classes.Set("dropping", false);
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is not { } items)
            return;

        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await _viewModel.Context.AddPathsAsync(paths);
    }
}
