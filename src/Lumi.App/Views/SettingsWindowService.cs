using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>設定視窗只會有一個：已開啟時帶到前面並切換到要去的位置。視窗不鎖住工作台。</summary>
public sealed class SettingsWindowService : ISettingsWindowService
{
    private readonly Func<SettingsWindow> _factory;
    private SettingsWindow? _window;
    private TaskCompletionSource? _closed;

    public SettingsWindowService(Func<SettingsWindow> factory)
    {
        _factory = factory;
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
