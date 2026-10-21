using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.App.Views.Responsive;

namespace Lumi.App.Views;

/// <summary>code-behind 只放視覺行為：啟動時載入、彈出選單的收合、關閉視窗的行為。Enter 送出與拖放在 <see cref="ChatComposer"/>，自動捲到底在 <see cref="ConversationView"/>。</summary>
public partial class WorkbenchWindow : Window
{
    private readonly WorkbenchViewModel _viewModel;

    public WorkbenchWindow(WorkbenchViewModel viewModel, IAppModeCoordinator? modes = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ResponsiveClasses.Track(this);

        // Enter 送出、貼上截圖、拖放在 ChatComposer；自動捲到底在 ConversationView
        // 選了模型就關掉彈出選單
        viewModel.ModelSwitcher.Selected += () => Toolbar.CapsuleButton.Flyout?.Hide();
        viewModel.RetrySwitcher.Selected += () => Conversation.RetryOtherModelButton.Flyout?.Hide();
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
        Conversation.ScrollToEnd();
        Composer.FocusInput();
    }
}
