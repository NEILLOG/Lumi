using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>code-behind 只放視窗行為：關閉前詢問有沒有未儲存的變更、把焦點放到金鑰輸入框。</summary>
public partial class SettingsWindow : Window
{
    private bool _closeConfirmed;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.Connections.FocusApiKeyRequested += () =>
            Dispatcher.UIThread.Post(() => KeyBox.Focus(), DispatcherPriority.Loaded);
    }

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
