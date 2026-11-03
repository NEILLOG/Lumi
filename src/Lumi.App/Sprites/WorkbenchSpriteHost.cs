using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 工作台角色的外框（M27）：大小是 <see cref="SpriteOptions.Outer"/>。只有角色本體接滑鼠（<see cref="ISpriteView.HitTest"/>），
/// 透明處穿透到下面的標題列。純裝飾：不能取得焦點，對輔助工具隱藏。
/// </summary>
public sealed class WorkbenchSpriteHost : Decorator, ICustomHitTest
{
    private readonly TranslateTransform _offset = new();
    private ISpriteView? _view;

    public WorkbenchSpriteHost()
    {
        Focusable = false;
        RenderTransform = _offset;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    public ISpriteView? View => _view;

    public double OffsetY => _offset.Y;

    public void Show(ISpriteView view, SpriteOptions options)
    {
        Child = null;
        _view = view;
        Width = options.Outer.Width;
        Height = options.Outer.Height;
        Child = view.Visual;
    }

    public void Clear()
    {
        Child = null;
        _view = null;
    }

    /// <summary>套用動作。偏移已經是整數，像素角色不會糊。</summary>
    public void ApplyMotion(double offsetY, double opacity)
    {
        _offset.Y = offsetY;
        Opacity = opacity;
    }

    public bool HitTest(Point point) => _view?.HitTest(point) ?? false;
}
