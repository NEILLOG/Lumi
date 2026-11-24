using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Markdown;
using Lumi.App.ViewModels;
using Lumi.Core.Branches;

namespace Lumi.App.Views;

/// <summary>
/// 訊息流。code-behind 只放視覺行為：在最底部時跟著新內容自動捲動，使用者往上捲就不打擾；
/// 離開底部時顯示「回到最新訊息」（FT-01）；Windows 上可以按住中鍵拖曳捲動（FT-02）。
/// 支線（M36）：「問這段」的浮動工具列（選取助理回答的文字、放開滑鼠後出現）、右鍵選單的「問這段」、進入支線的動畫。
/// <c>MessageScroller</c>、<c>RetryOtherModelButton</c> 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 直接使用。
/// </summary>
public partial class ConversationView : UserControl
{
    private const double StickToBottomTolerance = 24;

    private bool _stickToBottom = true;

    /// <summary>「問這段」浮動工具列開著時，要問的訊息與引用、要複製的 Markdown。</summary>
    private (MessageViewModel Message, string Quote)? _ask;
    private string? _askMarkdown;

    private WorkbenchViewModel? _branchVm;

    public ConversationView()
    {
        InitializeComponent();
        AutoScroll = new MiddleButtonAutoScroll(this, MessageScroller, () => MiddleButtonScrollEnabled);
        MessageScroller.ScrollChanged += OnMessageScrollChanged;
        JumpToBottomButton.Click += (_, _) => JumpToBottom();
        // 要收得到子元素冒泡上來的 GotFocus，所以用 AddHandler
        MessageScroller.AddHandler(GotFocusEvent, OnMessageGotFocus);

        // 「問這段」（E8）：放開滑鼠時看選取（MarkdownView 會先處理並標成已處理，所以要 handledEventsToo）；Esc 收起
        AddHandler(PointerReleasedEvent, OnAskPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape) CloseAsk(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(BranchSelectionEvents.AskRequestedEvent, (_, e) => AskFromMenu(e));
        AskCopyButton.Click += (_, _) => CopyAsk();
        AskButton.Click += (_, _) => SendAsk();

        // 進入支線的動畫：WorkbenchViewModel.BranchEntered（新建支線之後才發）；
        // 使用者送出（FX-14）：ScrollToLatestRequested，回到最下面並恢復跟著捲動
        DataContextChanged += (_, _) =>
        {
            if (_branchVm is not null)
            {
                _branchVm.BranchEntered -= OnBranchEntered;
                _branchVm.ScrollToLatestRequested -= OnScrollToLatestRequested;
            }
            _branchVm = DataContext as WorkbenchViewModel;
            if (_branchVm is not null)
            {
                _branchVm.BranchEntered += OnBranchEntered;
                _branchVm.ScrollToLatestRequested += OnScrollToLatestRequested;
            }
        };
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
        // 「問這段」的浮動工具列跟著捲動收起（E8）
        if (e.OffsetDelta != default)
            CloseAsk();

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

    // 使用者開始一輪回應（FX-14）：立刻回到底部、恢復跟著捲動；排版完成後（新訊息與氣泡已經加進列表）再捲一次，
    // 仿照 WorkbenchWindow.ScrollToCall 的寫法。延後的那次只在還跟著底部時才做：排隊期間使用者往上捲就不拉回去
    private void OnScrollToLatestRequested(object? sender, EventArgs e)
    {
        JumpToBottom();
        Dispatcher.UIThread.Post(() =>
        {
            if (_stickToBottom)
                JumpToBottom();
        }, DispatcherPriority.Loaded);
    }

    // ---- 問這段（M36 E8）----

    /// <summary>放開滑鼠：有可以問的選取就在滑鼠上方開浮動工具列；沒有（點擊、不能問的訊息）就收起。</summary>
    private void OnAskPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var view = e.InitialPressMouseButton == MouseButton.Left
            ? (e.Source as Visual)?.FindAncestorOfType<MarkdownView>(includeSelf: true)
            : null;
        var message = BranchSelectionEvents.AskableMessage(view);
        var quote = view is null ? null : BranchBlocks.NormalizeQuote(view.SelectedText);
        if (view is null || message is null || quote is null)
        {
            CloseAsk();
            return;
        }

        _ask = (message, quote);
        _askMarkdown = view.SelectedMarkdown;
        AskPopup.PlacementTarget = view;
        AskPopup.IsOpen = true;
    }

    /// <summary>右鍵選單的「問這段」（<see cref="BranchSelectionEvents.AskRequestedEvent"/>）。</summary>
    private void AskFromMenu(AskSelectionEventArgs e)
    {
        var view = (e.Source as Visual)?.FindAncestorOfType<MarkdownView>(includeSelf: true);
        var message = BranchSelectionEvents.AskableMessage(view);
        var quote = BranchBlocks.NormalizeQuote(e.Text);
        if (message is null || quote is null || DataContext is not WorkbenchViewModel vm)
            return;
        e.Handled = true;
        vm.AskAboutSelectionCommand.Execute(new BranchQuoteRequest(message, quote));
    }

    private void SendAsk()
    {
        var ask = _ask;
        CloseAsk();
        if (ask is not { } request || DataContext is not WorkbenchViewModel vm)
            return;
        vm.AskAboutSelectionCommand.Execute(new BranchQuoteRequest(request.Message, request.Quote));
    }

    private async void CopyAsk()
    {
        var markdown = _askMarkdown;
        CloseAsk();
        if (string.IsNullOrEmpty(markdown) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await MarkdownClipboard.SetAsync(clipboard, markdown);
        }
        catch (Exception)
        {
            // 剪貼簿被其他程式佔用時放棄這次複製（同 MarkdownView）
        }
    }

    private void CloseAsk()
    {
        AskPopup.IsOpen = false;
        _ask = null;
        _askMarkdown = null;
    }

    // ---- 進入支線的動畫（4.8「進入動畫」）----

    private void OnBranchEntered(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => _ = PlayBranchEnterAsync());

    /// <summary>色條 ScaleY 0→1（原點在上方，320ms，延遲 100ms）；出處卡片 Opacity 0→1、X -12→0（200ms，延遲 180ms）；CubicEaseOut。</summary>
    private async Task PlayBranchEnterAsync()
    {
        var rail = BranchRailGrow;
        var scale = rail.RenderTransform as ScaleTransform;
        var card = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "BranchOriginCard");
        var slide = card?.RenderTransform as TranslateTransform;
        if (scale is null)
            return;

        scale.ScaleY = 0;
        rail.IsVisible = true;
        var tasks = new List<Task>
        {
            Tween(TimeSpan.FromMilliseconds(320), TimeSpan.FromMilliseconds(100), ScaleTransform.ScaleYProperty, 0, 1).RunAsync(scale),
        };
        if (card is not null && slide is not null)
        {
            card.Opacity = 0;
            slide.X = -12;
            tasks.Add(Tween(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(180), Visual.OpacityProperty, 0, 1).RunAsync(card));
            tasks.Add(Tween(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(180), TranslateTransform.XProperty, -12, 0).RunAsync(slide));
        }

        await Task.WhenAll(tasks);
        rail.IsVisible = false;
    }

    private static Animation Tween(TimeSpan duration, TimeSpan delay, AvaloniaProperty property, double from, double to) => new()
    {
        Duration = duration,
        Delay = delay,
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(property, from) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(property, to) } },
        },
    };
}
