using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.App.Views;

/// <summary>
/// 真的視窗（<see cref="IWindowHost"/> 的正式實作）。視窗用到才從 DI 取（延遲建立），
/// 所以協調者、工作台與小精靈之間不會有建構時的循環相依。
/// </summary>
public sealed class WindowHost(IServiceProvider services) : IWindowHost
{
    private WorkbenchWindow? _workbench;

    private WorkbenchWindow Workbench => _workbench ??= services.GetRequiredService<WorkbenchWindow>();

    private SpriteWindow Sprite => services.GetRequiredService<SpriteWindow>();

    public bool IsWorkbenchVisible => _workbench?.IsVisible ?? false;

    public bool IsSpriteVisible => Sprite.IsVisible;

    public void ShowWorkbench()
    {
        var window = Workbench;
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void HideWorkbench() => _workbench?.Hide();

    public void ShowSprite()
    {
        var window = Sprite;
        if (!window.IsVisible)
            window.Show();
    }

    public void HideSprite() => Sprite.Hide();

    public void ScrollWorkbenchToCall(string callId) => Workbench.ScrollToCall(callId);

    public void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
