using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Core.Platform;
using Lumi.Core.Reminders;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>小精靈的提醒卡片（headless）：出現、不因時間消失、與回應共存、關閉後顯示下一則。</summary>
public class SpriteReminderHeadlessTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);
    private static readonly Point Far = new(2000, 2000);

    private sealed class QuietOverlay : IOverlayPlatform
    {
        public string Name => "Quiet";
        public bool IsSupported => true;
        public bool ClickThroughActive { get; private set; }

        public void Attach(nint nativeWindowHandle) { }

        public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out OverlayPoint dip)
        {
            dip = new OverlayPoint(Far.X, Far.Y);
            return true;
        }

        public void SetClickThrough(bool on) => ClickThroughActive = on;
    }

    private sealed class Rig
    {
        public Rig(SpriteShellOptions? options = null)
        {
            Center = new NotificationCenter(new SyncUiDispatcher(), () => Windows, () => throw new InvalidOperationException("不應該要求小精靈"),
                NullLogger<NotificationCenter>.Instance);
            var catalog = new SpriteCatalog([new OrbSpriteFactory()]);
            Window = new SpriteWindow(catalog, new QuietOverlay(), Settings, options ?? new SpriteShellOptions(), Time, NullLogger<SpriteWindow>.Instance,
                notifications: Center);
            Windows.ShowSprite();   // 小精靈可見：提醒顯示在小精靈上，不要求別的視窗
            Window.Show();
        }

        public FakeWindowHost Windows { get; } = new();

        public FakeModelCatalog Settings { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public NotificationCenter Center { get; }

        public SpriteWindow Window { get; }

        public SpriteBubbleView Bubble => Window.FindControl<SpriteBubbleView>("Bubble")!;

        public void Poll()
        {
            Time.Advance(TimeSpan.FromMilliseconds(50));
            Window.Poll();
            Window.UpdateLayout();
        }

        public void Add(string id) => Center.ShowAsync(new AppNotification(id, $"標題 {id}", $"內容 {id}", At, NotificationKind.Reminder), default);

        public Border Card => Bubble.FindControl<Border>("ReminderCard")!;

        public Button CloseButton => Bubble.FindControl<Button>("ReminderDismissButton")!;
    }

    [AvaloniaFact]
    public void AVeryTallBubble_StaysBelowTheWindowTop_SoItsShadowIsNotCut()
    {
        // 預留給氣泡的高度設得很小，讓一般的提醒卡片就超過上限
        var rig = new Rig(new SpriteShellOptions { BubbleReserve = 60 });
        rig.Center.ShowAsync(new AppNotification("long", "提醒", string.Concat(Enumerable.Repeat("內容 ", 200)), At, NotificationKind.Reminder), default);
        rig.Poll();

        var top = rig.Bubble.TranslatePoint(new Point(0, 0), rig.Window)!.Value.Y;
        Assert.True(top >= SpriteBubbleView.ShadowTop - 0.5, $"氣泡上緣 {top} 太靠近視窗頂端");
        Assert.True(rig.Bubble.Bounds.Height <= rig.Bubble.MaxHeight + 0.5);
        var scroll = rig.Bubble.FindControl<ScrollViewer>("BubbleScroll")!;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "內容比上限高時要能捲動");
        Assert.Equal(scroll.Extent.Height - scroll.Viewport.Height, scroll.Offset.Y, 1);   // 停在最下面
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheReminderCard_ShowsTheNewest_AndDoesNotExpire()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Poll();

        Assert.True(rig.Card.IsVisible);
        Assert.Equal("標題 a", rig.Bubble.FindControl<Avalonia.Controls.TextBlock>("ReminderTitle")!.Text);

        rig.Time.Advance(TimeSpan.FromMinutes(30));
        rig.Poll();

        Assert.True(rig.Card.IsVisible, "提醒不能自動消失");
        Assert.True(rig.Bubble.FindControl<Border>("BubbleBorder")!.IsVisible || rig.Card.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Closing_ShowsTheNextOne_AndTheCardDisappearsWhenNoneIsLeft()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("b");
        rig.Poll();

        Assert.Equal("標題 b", rig.Bubble.FindControl<Avalonia.Controls.TextBlock>("ReminderTitle")!.Text);
        Assert.True(rig.Bubble.FindControl<Avalonia.Controls.TextBlock>("ReminderMore")!.IsVisible);

        rig.CloseButton.Command!.Execute(null);
        rig.Poll();
        Assert.Equal("標題 a", rig.Bubble.FindControl<Avalonia.Controls.TextBlock>("ReminderTitle")!.Text);
        Assert.False(rig.Bubble.FindControl<Avalonia.Controls.TextBlock>("ReminderMore")!.IsVisible);

        rig.CloseButton.Command!.Execute(null);
        rig.Poll();
        Assert.False(rig.Card.IsVisible);
        Assert.False(rig.Window.BubbleViewModel.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheReminderCard_CoexistsWithAStreamingAnswer()
    {
        var rig = new Rig();
        rig.Add("a");
        var bubble = rig.Window.BubbleViewModel;
        bubble.OnRunStarted();
        bubble.OnEvent(new TextDelta("回答中"));
        rig.Poll();

        Assert.True(rig.Card.IsVisible);
        Assert.True(rig.Bubble.FindControl<Border>("BubbleBorder")!.IsVisible);
        Assert.Equal("回答中", bubble.Text);
        rig.Window.Close();
    }
    [AvaloniaFact]
    public void ReminderPlusLongestAnswer_FitsInsideTheBubbleArea()
    {
        var rig = new Rig();
        rig.Center.ShowAsync(new AppNotification("long", new string('標', 60), new string('字', 300), At, NotificationKind.Reminder), default);
        var bubble = rig.Window.BubbleViewModel;
        bubble.OnRunStarted();
        bubble.OnEvent(new TextDelta(new string('答', 300)));   // 超過上限：截斷 120 字並顯示完整內容連結（最長的回應）
        rig.Poll();
        rig.Window.UpdateLayout();

        var area = rig.Window.FindControl<Grid>("BubbleArea")!;
        var top = rig.Bubble.TranslatePoint(new Point(0, 0), rig.Window)!.Value.Y;
        Assert.True(rig.Bubble.Bounds.Height > 150, $"量測無效：氣泡高度={rig.Bubble.Bounds.Height}");   // 確認真的排版過
        Assert.True(top >= 0, $"氣泡超出上方：top={top}，氣泡區高度={area.Bounds.Height}，氣泡高度={rig.Bubble.Bounds.Height}");
        rig.Window.Close();
    }
}
