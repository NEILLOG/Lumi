using Avalonia.Controls;
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
        ResponsiveClasses.Track(this);
        viewModel.Connections.FocusApiKeyRequested += () =>
            Dispatcher.UIThread.Post(() => KeyBox.Focus(), DispatcherPriority.Loaded);

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
        };
        _preview.Show(viewModel.Sprite.PreviewFactory);
        if (viewModel.Sprite.PreviewState is { } initial)
            _preview.SetState(initial.State);
        Opened += (_, _) => UpdatePreviewRunning();
        Closed += (_, _) => _preview.Dispose();
    }

    private void UpdatePreviewRunning()
    {
        if (ViewModel.ShowSprite && IsVisible)
            _preview?.Start();
        else
            _preview?.Stop();
    }

    internal SpritePreviewHost? Preview => _preview;

    public SettingsViewModel ViewModel { get; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && ViewModel.HasUnsavedChanges)
        {
            e.Cancel = true;
            _ = ConfirmAndCloseAsync();
            return;
        }
        base.OnClosing(e);
    }

    private async Task ConfirmAndCloseAsync()
    {
        if (await ViewModel.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
