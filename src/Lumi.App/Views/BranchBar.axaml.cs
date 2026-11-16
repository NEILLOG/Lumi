using System.Collections.Specialized;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 工作台的支線路徑列（M36 36F）。code-behind 只做視覺行為：視窗寬度分段（window 的 wide／compact／narrow class → <see cref="BranchBarViewModel.WidthClass"/>）
/// 與進入動畫（4.8：路徑列往下展開 180ms，只在新開支線時播）。
/// </summary>
public partial class BranchBar : UserControl
{
    private Window? _window;

    public BranchBar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 新開支線時播放：不透明度 0→1、往下位移 -8→0，180ms，CubicEaseOut。位移用上邊距做（Avalonia 不接受把動畫直接下在 Transform 上）。
    /// 不偵測系統的減少動態效果（計畫 C6）。
    /// </summary>
    public void PlayEnterAnimation()
    {
        _ = FadeIn().RunAsync(this);
        _ = SlideDown().RunAsync(this);
    }

    private static Animation FadeIn() => new()
    {
        Duration = TimeSpan.FromMilliseconds(180),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
        },
    };

    private static Animation SlideDown() => new()
    {
        Duration = TimeSpan.FromMilliseconds(180),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(MarginProperty, new Thickness(0, -8, 0, 0)) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(MarginProperty, new Thickness(0)) } },
        },
    };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
            _window.Classes.CollectionChanged += OnWindowClassesChanged;
        ApplyWidthClass();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null)
            _window.Classes.CollectionChanged -= OnWindowClassesChanged;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyWidthClass();
    }

    private void OnWindowClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyWidthClass();

    private void ApplyWidthClass()
    {
        if (DataContext is not BranchBarViewModel model)
            return;
        var classes = _window?.Classes;
        model.WidthClass = classes is null ? "wide"
            : classes.Contains("narrow") ? "narrow"
            : classes.Contains("compact") ? "compact"
            : "wide";
    }
}
