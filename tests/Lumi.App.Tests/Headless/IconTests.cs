using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Lumi.App.Controls;

namespace Lumi.App.Tests.Headless;

/// <summary>UA-8：<c>Themes/Icons.axaml</c> 的每個圖示都能解析成 24x24 幾何，<see cref="LumiIcon"/> 依 Foreground 描邊。</summary>
public class IconTests
{
    /// <summary>計畫要求的圖示；少一個就代表 Icons.axaml 漏了。</summary>
    public static readonly string[] RequiredKeys =
    [
        "LumiIconChevronDown", "LumiIconPlus", "LumiIconClock", "LumiIconMoreHorizontal", "LumiIconSparkle",
        "LumiIconArrowUp", "LumiIconSquare", "LumiIconPaperclip", "LumiIconFolder", "LumiIconMic", "LumiIconCopy",
        "LumiIconRefresh", "LumiIconThumbsUp", "LumiIconThumbsDown", "LumiIconX", "LumiIconCheck", "LumiIconSettings",
        "LumiIconArrowLeft", "LumiIconHome", "LumiIconDiamond", "LumiIconSun", "LumiIconShield", "LumiIconAlertTriangle",
        "LumiIconUser", "LumiIconChevronRight", "LumiIconBan", "LumiIconLoader",
    ];

    private static Geometry Icon(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value), $"找不到圖示資源 {key}");
        return Assert.IsAssignableFrom<Geometry>(value);
    }

    [AvaloniaFact]
    public void EveryRequiredIcon_ResolvesToGeometryInside24x24()
    {
        foreach (var key in RequiredKeys)
        {
            var geometry = Icon(key);
            var bounds = geometry.Bounds;
            Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{key} 是空的");
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24,
                $"{key} 超出 24x24 座標：{bounds}");
        }
    }

    [AvaloniaFact]
    public void EveryIconInTheFile_IsGeometry_AndStartsWithLumiIcon()
    {
        var dictionary = new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Lumi.App/"))
        {
            Source = new Uri("avares://Lumi.App/Themes/Icons.axaml"),
        }.Loaded;
        var keys = dictionary.Keys.Select(k => k?.ToString() ?? "").ToList();
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.StartsWith("LumiIcon", key, StringComparison.Ordinal);
            Assert.IsAssignableFrom<Geometry>(dictionary[key]);
        }
        Assert.All(RequiredKeys, key => Assert.Contains(key, keys));
    }

    [AvaloniaFact]
    public void LumiIcon_DefaultsAndSize()
    {
        var icon = new LumiIcon { Data = Icon("LumiIconPlus") };
        Assert.Equal(16, icon.Size);
        Assert.Equal(1.75, icon.StrokeThickness);

        var host = new Window { Width = 200, Height = 200, Content = new StackPanel { Children = { icon } } };
        host.Show();
        host.UpdateLayout();
        Assert.Equal(new Size(16, 16), icon.Bounds.Size);

        icon.Size = 24;
        host.UpdateLayout();
        Assert.Equal(new Size(24, 24), icon.Bounds.Size);
    }

    [AvaloniaFact]
    public void LumiIcon_StrokesWithForeground_RoundCapsAndJoins_AndFollowsChanges()
    {
        var icon = new LumiIcon { Data = Icon("LumiIconCheck"), Foreground = Brushes.Red };
        var pen = Assert.IsType<Pen>(icon.CreatePen());
        Assert.Same(Brushes.Red, pen.Brush);
        Assert.Equal(1.75, pen.Thickness);
        Assert.Equal(PenLineCap.Round, pen.LineCap);
        Assert.Equal(PenLineJoin.Round, pen.LineJoin);

        icon.Foreground = Brushes.Blue;
        icon.StrokeThickness = 2;
        pen = Assert.IsType<Pen>(icon.CreatePen());
        Assert.Same(Brushes.Blue, pen.Brush);
        Assert.Equal(2, pen.Thickness);
    }

    [AvaloniaFact]
    public void LumiIcon_InheritsForegroundFromParent_SoItFollowsTheButtonText()
    {
        var icon = new LumiIcon { Data = Icon("LumiIconX") };
        var button = new Button { Foreground = Brushes.Green, Content = icon };
        var window = new Window { Content = button };
        window.Show();
        window.UpdateLayout();
        Assert.Same(Brushes.Green, icon.Foreground);
        Assert.Same(Brushes.Green, Assert.IsType<Pen>(icon.CreatePen()).Brush);

        button.Foreground = Brushes.Orange;
        Assert.Same(Brushes.Orange, Assert.IsType<Pen>(icon.CreatePen()).Brush);
    }

    [AvaloniaFact]
    public void LumiIcon_NullForeground_DrawsNothing()
    {
        Assert.Null(new LumiIcon { Data = Icon("LumiIconX"), Foreground = null }.CreatePen());
    }
}
