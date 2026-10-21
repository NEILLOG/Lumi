using Lumi.App.Services;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>設定視窗只會有一個：已開啟時帶到前面並切換到要去的位置。視窗不鎖住工作台。</summary>
public sealed class SettingsWindowService : ISettingsWindowService
{
    private readonly Func<SettingsWindow> _factory;
    private readonly IAppModeCoordinator _modes;
    private SettingsWindow? _window;
    private TaskCompletionSource? _closed;

    public SettingsWindowService(Func<SettingsWindow> factory, IAppModeCoordinator modes)
    {
        _factory = factory;
        _modes = modes;
    }

    public async Task<bool> ReturnToWorkbench()
    {
        // 先處理未儲存變更的詢問（和按右上角關閉同一條流程），取消就留在設定視窗
        if (_window is { } window && !await window.TryCloseAsync())
            return false;

        _modes.OpenWorkbench();
        return true;
    }

    public async Task ShowAsync(SettingsNavigation navigation)
    {
        if (_window is null)
        {
            var window = _factory();
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) =>
            {
                _window = null;
                closed.TrySetResult();
            };
            _window = window;
            _closed = closed;
            window.Show();
        }

        var current = _window!;
        var closedTask = _closed!.Task;
        current.Activate();
        await current.ViewModel.NavigateAsync(navigation);
        await closedTask;
    }
}
