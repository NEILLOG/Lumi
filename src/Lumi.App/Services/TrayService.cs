using Avalonia;
using Avalonia.Controls;
using Lumi.App.Resources;
using Lumi.App.Sprites;

namespace Lumi.App.Services;

/// <summary>
/// 托盤圖示（Windows 通知區域、macOS 選單列），三項：顯示小精靈、開啟工作台、結束 Lumi。
/// 小精靈被「暫時隱藏」或只剩小精靈時，這是唯一能叫回視窗與結束程式的地方（M7 的 Q3）。
/// </summary>
public sealed class TrayService
{
    private TrayIcon? _icon;

    public void Attach(Application application, Action showSprite, Action openWorkbench, Action exit)
    {
        if (_icon is not null)
            return;

        var show = new NativeMenuItem(Strings.Sprite_TrayShowSprite);
        show.Click += (_, _) => showSprite();
        var workbench = new NativeMenuItem(Strings.Sprite_TrayOpenWorkbench);
        workbench.Click += (_, _) => openWorkbench();
        var quit = new NativeMenuItem(Strings.Sprite_TrayExit);
        quit.Click += (_, _) => exit();

        _icon = new TrayIcon
        {
            Icon = TrayIconFactory.CreateOrbIcon(),
            ToolTipText = Strings.Sprite_TrayTooltip,
            Menu = [show, workbench, new NativeMenuItemSeparator(), quit],
            IsVisible = true,
        };
        TrayIcon.SetIcons(application, [_icon]);
    }
}
