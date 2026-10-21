using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Lumi.App.Views;

/// <summary>
/// 自訂標題列（RWD原則 3.1）：Lumi 標誌、可拖曳的空白、「小精靈模式」按鈕。視窗要設
/// <c>ExtendClientAreaToDecorationsHint=True</c>；系統按鈕（Windows 右側、macOS 左側）由系統繪製，
/// 這裡只為它們保留空間。排列規則在 <see cref="TitleBarLayout"/>（純函式），這裡只做視覺行為：拖曳、雙擊最大化。
/// </summary>
/// <remarks>x:Name 的控制項（<c>SpriteModeButton</c> 等）由 Avalonia 產生同名欄位，同一個 View 內可直接使用。</remarks>
public partial class CustomTitleBar : UserControl
{
    public static readonly StyledProperty<TitleBarPlatform> PlatformProperty =
        AvaloniaProperty.Register<CustomTitleBar, TitleBarPlatform>(nameof(Platform), TitleBarLayout.CurrentPlatform);

    public CustomTitleBar()
    {
        InitializeComponent();
        ApplyLayout(Platform);
        TitleBarSurface.PointerPressed += OnSurfacePointerPressed;
    }

    /// <summary>排列用的平台，預設是目前的作業系統；測試可以指定另一個平台。</summary>
    public TitleBarPlatform Platform
    {
        get => GetValue(PlatformProperty);
        set => SetValue(PlatformProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlatformProperty)
            ApplyLayout(change.GetNewValue<TitleBarPlatform>());
    }

    private void ApplyLayout(TitleBarPlatform platform)
    {
        var layout = TitleBarLayout.For(platform);
        Classes.Set("mac", platform == TitleBarPlatform.MacOS);
        Classes.Set("win", platform == TitleBarPlatform.Windows);
        LeftSpacer.Width = layout.LeftInset;
        RightSpacer.Width = layout.RightInset;
        Divider.IsVisible = layout.ShowDivider;
    }

    /// <summary>點在按鈕（或按鈕裡的內容）上時不是拖曳。</summary>
    internal static bool IsOnInteractiveControl(object? source) =>
        source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() is not null);

    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsOnInteractiveControl(e.Source) || TopLevel.GetTopLevel(this) is not Window window)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
            window.WindowState = TitleBarLayout.NextStateOnDoubleClick(window.WindowState, window.CanResize);
        else
            window.BeginMoveDrag(e);
    }
}
