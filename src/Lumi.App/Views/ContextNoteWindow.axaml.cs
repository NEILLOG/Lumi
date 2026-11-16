using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lumi.App.ViewModels;
using Lumi.App.Views.Responsive;

namespace Lumi.App.Views;

/// <summary>
/// 研究本視窗（M35 35G）的 code-behind：只放視窗行為（分段切換的方向鍵、並排按鈕的事件、編輯中關閉前先存檔）。
/// 服務（<see cref="ContextNoteWindowService"/>）負責開關、跟著工作台換對話、位置記憶。
/// </summary>
public partial class ContextNoteWindow : Window
{
    private bool _closeConfirmed;

    public ContextNoteWindow(ContextNoteViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ResponsiveClasses.Track(this);

        // 分段切換是單選群組：Tab 只停在已選的那一段，方向鍵換段（同設定視窗）
        ModeSwitchPanel.AddHandler(KeyDownEvent, OnSegmentKeyDown, RoutingStrategies.Tunnel);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ContextNoteViewModel.IsPreview) or nameof(ContextNoteViewModel.IsEdit))
                UpdateSegmentTabStop();
        };
        UpdateSegmentTabStop();
    }

    public ContextNoteViewModel ViewModel { get; }

    /// <summary>「並排」按鈕被按（服務訂閱，計算版面）。</summary>
    public event EventHandler? SideBySideRequested;

    private void OnSideBySideClick(object? sender, RoutedEventArgs e) => SideBySideRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 編輯中按關閉鈕：先存檔（超過字數就不存），再關閉
        if (!_closeConfirmed && ViewModel.IsEdit)
        {
            e.Cancel = true;
            _ = CloseWindowAsync();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>離開編輯（存檔或不存）後關閉視窗。</summary>
    internal async Task CloseWindowAsync()
    {
        await ViewModel.LeaveEditAsync();
        _closeConfirmed = true;
        Close();
    }

    private void UpdateSegmentTabStop()
    {
        var current = ViewModel.IsEdit ? EditSegment : PreviewSegment;
        KeyboardNavigation.SetTabOnceActiveElement(ModeSwitchPanel, current);
    }

    private void OnSegmentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
            return;
        var segments = new[] { PreviewSegment, EditSegment };
        var index = Array.FindIndex(segments, s => s.IsFocused);
        if (index < 0)
            return;
        var step = e.Key is Key.Right or Key.Down ? 1 : -1;
        var target = segments[(index + step + segments.Length) % segments.Length];
        target.Focus();
        target.Command?.Execute(target.CommandParameter);
        UpdateSegmentTabStop();
        e.Handled = true;
    }
}
