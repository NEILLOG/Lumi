using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>
/// UE-1：全域 TextBox 樣式（最小高度 40、Padding、Focus 光暈）不能改變小精靈輸入列的版面。
/// M6 6E 加了麥克風鈕，輸入列重排（新的座標與理由見 docs/ui-spec.md 8.2、plan/06-speech-whisper.md 6E）：
/// 輸入列仍是 300×48；麥克風 36×36 在 (7,6)；輸入框縮成 198×12 在 (51,18)；送出鈕 36×36 在 (257,6)。
/// </summary>
public class SpriteInputBarLayoutTests
{
    private sealed class NoNativeOverlay : Lumi.Core.Platform.IOverlayPlatform
    {
        public string Name => "none";
        public bool IsSupported => false;
        public bool ClickThroughActive => false;
        public void Attach(nint nativeWindowHandle) { }
        public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out Lumi.Core.Platform.OverlayPoint dip) { dip = default; return false; }
        public void SetClickThrough(bool on) { }
    }

    private static SpriteWindow Show()
    {
        var window = new SpriteWindow(new SpriteCatalog([new OrbSpriteFactory()]), new NoNativeOverlay(), new FakeModelCatalog(),
            new SpriteShellOptions(), new FakeTimeProvider(), NullLogger<SpriteWindow>.Instance);
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static void AssertLayoutUnchanged(SpriteWindow window)
    {
        var bar = window.FindControl<Border>("InputBar")!;
        var input = window.FindControl<TextBox>("Input")!;
        var send = window.FindControl<Button>("SendButton")!;

        var mic = window.FindControl<Button>("MicButton")!;

        Assert.Equal(new Size(300, 48), bar.Bounds.Size);
        Assert.Equal(new Size(36, 36), mic.Bounds.Size);
        Assert.Equal(new Point(7, 6), mic.TranslatePoint(default, bar));
        Assert.Equal(new Size(198, 12), input.Bounds.Size);
        Assert.Equal(new Point(51, 18), input.TranslatePoint(default, bar));
        Assert.Equal(new Size(36, 36), send.Bounds.Size);
        Assert.Equal(new Point(257, 6), send.TranslatePoint(default, bar));
    }

    [AvaloniaFact]
    public void TheInputBar_KeepsItsSizeAndPositions_AfterTheGlobalTextBoxStyle()
    {
        var window = Show();
        AssertLayoutUnchanged(window);
        window.Close();
    }

    [AvaloniaFact]
    public void TheInputBar_KeepsItsLayout_WithTextAndFocus_AndShowsNoBorderOrGlow()
    {
        var window = Show();
        var input = window.FindControl<TextBox>("Input")!;
        input.Text = "你好 Lumi";
        input.Focus();
        window.UpdateLayout();

        AssertLayoutUnchanged(window);
        // 全域樣式的 MinHeight 40、Padding 12,8、框線與 Focus 光暈都不能出現在輸入列裡
        Assert.Contains("embedded", input.Classes);
        Assert.Equal(0, input.MinHeight);
        Assert.Equal(new Thickness(0), input.Padding);
        var border = input.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
        Assert.Equal(new Thickness(0), border.BorderThickness);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)border.Background!).Color);
        Assert.False(input.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_FocusGlow").IsVisible);
        window.Close();
    }

    private static void AssertBackground(SpriteWindow window, Button button, string tokenKey)
    {
        Assert.True(window.TryFindResource(tokenKey, window.ActualThemeVariant, out var token), tokenKey);
        Assert.Equal(((ISolidColorBrush)token!).Color, ((ISolidColorBrush)button.Background!).Color);
    }

    [AvaloniaFact]
    public void TheSendButton_UsesLumiIcons_AccentWhenSending_AndPrimaryWhenStopping()
    {
        var window = Show();
        var send = window.FindControl<Button>("SendButton")!;
        var sendIcon = window.FindControl<LumiIcon>("SendIcon")!;
        var stopIcon = window.FindControl<LumiIcon>("StopIcon")!;

        Assert.True(sendIcon.IsVisible);
        Assert.False(stopIcon.IsVisible);
        AssertBackground(window, send, "LumiAccentBrush");

        window.SetResponding(true);
        window.UpdateLayout();
        Assert.False(sendIcon.IsVisible);
        Assert.True(stopIcon.IsVisible);
        AssertBackground(window, send, "LumiPrimaryButtonBrush");
        AssertLayoutUnchanged(window);

        window.SetResponding(false);
        AssertBackground(window, send, "LumiAccentBrush");
        window.Close();
    }
}
