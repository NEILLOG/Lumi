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

    /// <summary>工作台顯示中時回它所在螢幕的工作區與縮放（研究本的「並排」，M35）；沒顯示時回 null。不會因此建立工作台視窗。</summary>
    public WindowScreen? GetWorkbenchScreen()
    {
        if (_workbench is not { IsVisible: true } window)
            return null;
        var screen = window.Screens.ScreenFromWindow(window);
        return screen is null ? null : new WindowScreen(screen.WorkingArea, screen.Scaling);
    }

    /// <summary>把工作台還原成一般大小並放到指定位置（並排用）。位置是螢幕像素，寬高是 DIP。</summary>
    public void PlaceWorkbench(PixelPoint position, double width, double height)
    {
        var window = Workbench;
        window.WindowState = WindowState.Normal;
        window.Position = position;
        window.Width = width;
        window.Height = height;
    }

    public void Exit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
