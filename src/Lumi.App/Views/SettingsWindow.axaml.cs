using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Lumi.App.Sprites;
using Lumi.App.ViewModels;
using Lumi.App.Views.Responsive;

namespace Lumi.App.Views;

/// <summary>code-behind 只放視窗行為：關閉前詢問有沒有未儲存的變更、把焦點放到金鑰輸入框。</summary>
public partial class SettingsWindow : Window
{
    private bool _closeConfirmed;
    private SpritePreviewHost? _preview;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ResponsiveClasses.Track(this, trackStacked: true);
        // 色票卡片排在可換行的 WrapPanel：左右鍵由 ListBox 處理，上下鍵要自己找「上一列／下一列」的卡片
        PaletteBox.AddHandler(KeyDownEvent, OnPaletteKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // 分段切換（連線／模型設定）是單選群組：Tab 只停在已選的那一段，方向鍵換段
        TabSwitchPanel.AddHandler(KeyDownEvent, OnSegmentKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        UpdateSegmentTabStop();
        // 筆記資料夾（M10 10B）：沒有儲存鈕，Enter 或離開欄位時套用
        NotesFolderBox.KeyDown += OnNotesFolderKeyDown;
        NotesFolderBox.LostFocus += (_, _) => viewModel.General.CommitNotesFolderCommand.Execute(null);
        viewModel.Connections.FocusApiKeyRequested += () =>
            Dispatcher.UIThread.Post(() => ConnectionsPanel.FocusKeyBox(), DispatcherPriority.Loaded);

        // 「小精靈」分頁的預覽區：角色或狀態一變就更新；只在這個分頁可見時才驅動畫面
        _preview = new SpritePreviewHost(SpritePreviewHost, this);
        viewModel.Sprite.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SpriteSettingsViewModel.PreviewFactory))
            {
                _preview.Show(viewModel.Sprite.PreviewFactory);
                viewModel.Sprite.RefreshIssues();   // 素材型角色的圖第一次用到才解碼，解碼時發現的問題（例如內容貼邊）這時才有
            }
            else if (e.PropertyName == nameof(SpriteSettingsViewModel.PreviewState) && viewModel.Sprite.PreviewState is { } item)
            {
                _preview.SetState(item.State);
                viewModel.Sprite.RefreshIssues();
            }
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ShowSprite))
                UpdatePreviewRunning();
            else if (e.PropertyName == nameof(SettingsViewModel.TabIndex))
                UpdateSegmentTabStop();
        };
        _preview.Show(viewModel.Sprite.PreviewFactory);
        if (viewModel.Sprite.PreviewState is { } initial)
            _preview.SetState(initial.State);
        Opened += (_, _) => UpdatePreviewRunning();
        Closed += (_, _) => _preview.Dispose();
    }

    private IReadOnlyList<RadioButton> Segments => [TabConnections, TabModels];

    /// <summary>Tab 進入分段切換時落在已選的那一段（沒有選取時落在第一段）。</summary>
    private void UpdateSegmentTabStop()
    {
        var current = Segments.FirstOrDefault(s => s.IsChecked == true) ?? Segments[0];
        KeyboardNavigation.SetTabOnceActiveElement(TabSwitchPanel, current);
    }

    private void OnSegmentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;
        var segments = Segments;
        var index = segments.ToList().FindIndex(s => s.IsFocused);
        if (index < 0)
            return;
        var step = e.Key is Key.Right or Key.Down ? 1 : -1;
        var target = segments[(index + step + segments.Count) % segments.Count];
        target.IsChecked = true;
        target.Focus();
        UpdateSegmentTabStop();
        e.Handled = true;
    }

    private void UpdatePreviewRunning()
    {
        if (ViewModel.ShowSprite && IsVisible)
            _preview?.Start();
        else
            _preview?.Stop();
    }

    internal SpritePreviewHost? Preview => _preview;

    private void OnNotesFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;
        ViewModel.General.CommitNotesFolderCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down))
            return;
        var items = Enumerable.Range(0, PaletteBox.ItemCount).Select(PaletteBox.ContainerFromIndex).OfType<ListBoxItem>().ToList();
        var current = items.FirstOrDefault(i => i.IsFocused) ?? items.FirstOrDefault(i => i.IsSelected);
        if (current is null)
            return;
        var down = e.Key == Key.Down;
        var otherRows = items.Where(i => down ? i.Bounds.Top > current.Bounds.Top + 1 : i.Bounds.Top < current.Bounds.Top - 1).ToList();
        if (otherRows.Count == 0)
            return;
        var rowTop = down ? otherRows.Min(i => i.Bounds.Top) : otherRows.Max(i => i.Bounds.Top);
        var target = otherRows.Where(i => Math.Abs(i.Bounds.Top - rowTop) <= 1)
            .MinBy(i => Math.Abs(i.Bounds.Center.X - current.Bounds.Center.X));
        if (target is null)
            return;
        PaletteBox.SelectedIndex = items.IndexOf(target);
        target.Focus();
        e.Handled = true;
    }

    public SettingsViewModel ViewModel { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && ViewModel.HasUnsavedChanges)
        {
            e.Cancel = true;
            _ = TryCloseAsync();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>有未儲存的變更就先詢問；確認（或沒有變更）就關閉視窗。使用者取消回 false，視窗留著。</summary>
    internal async Task<bool> TryCloseAsync()
    {
        if (!await ViewModel.ConfirmCloseAsync())
            return false;
        _closeConfirmed = true;
        Close();
        return true;
    }
}
