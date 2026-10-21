using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Models;
using Lumi.Core.Platform;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>
/// 「照著 docs/sprite-dev-guide.md 新增一個角色，不需要修改殼」：測試專案裡一個最小的假角色
/// （只畫一個色塊、只支援 Idle），用 <c>AddSprite&lt;T&gt;()</c> 註冊、在設定選用，殼與設定頁照常運作。
/// </summary>
public class ChangingTheSpriteTests
{
    private sealed class QuietOverlay : IOverlayPlatform
    {
        public string Name => "none";
        public bool IsSupported => false;
        public bool ClickThroughActive => false;
        public void Attach(nint nativeWindowHandle) { }
        public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out OverlayPoint dip) { dip = default; return false; }
        public void SetClickThrough(bool on) { }
    }

    private sealed record Rig(SpriteWindow Window, FakeModelCatalog Catalog, BlockSpriteFactory Block);

    private static Rig CreateRig(SpriteSettings? settings = null)
    {
        var block = new BlockSpriteFactory();
        // 正式程式註冊角色的方式（App 註冊光球，第三方／之後的角色一樣用 AddSprite）
        var services = new ServiceCollection()
            .AddSprite<OrbSpriteFactory>()
            .AddSingleton<ISpriteFactory>(block)
            .BuildServiceProvider();
        var catalog = new FakeModelCatalog();
        if (settings is not null)
            catalog.Current = catalog.Current with { Sprite = settings };
        var window = new SpriteWindow(services.GetRequiredService<ISpriteCatalog>(), new QuietOverlay(), catalog, new SpriteShellOptions(),
            new FakeTimeProvider(), NullLogger<SpriteWindow>.Instance);
        return new Rig(window, catalog, block);
    }

    [Fact]
    public void AddSprite_RegistersTheTestSprite_NextToTheOrb()
    {
        var services = new ServiceCollection().AddSprite<OrbSpriteFactory>().AddSprite<TestBlock>().BuildServiceProvider();

        Assert.Equal(["lumi.orb", BlockSpriteFactory.SpriteId], services.GetRequiredService<ISpriteCatalog>().All.Select(f => f.Manifest.Id));
    }

    private sealed class TestBlock : ISpriteFactory
    {
        private readonly BlockSpriteFactory _inner = new();
        public SpriteManifest Manifest => _inner.Manifest;
        public ISpriteView Create(SpriteOptions options) => _inner.Create(options);
    }

    [AvaloniaFact]
    public void TheShell_RunsTheTestSprite_WithoutAnyChange()
    {
        var rig = CreateRig(SpriteSettings.Default with { SpriteId = BlockSpriteFactory.SpriteId });

        // 殼用角色宣告的框建出視窗，Visual 是角色的色塊
        var options = SpriteLimits.Fit(rig.Block.Manifest, SpriteLimits.DefaultTier).Options;
        Assert.Equal(options.Outer.Width, rig.Window.View.Visual.Width);
        Assert.IsType<Border>(rig.Window.View.Visual);
        var layout = rig.Window.Layout;
        Assert.True(rig.Window.View.HitTest(new Point(options.Outer.Width / 2, options.Outer.Height / 2)));
        Assert.False(rig.Window.View.HitTest(new Point(1, 1)));
        Assert.True(layout.WindowSize.Width >= 340);   // 殼的最小寬度，氣泡與輸入列放得下

        // 它只支援 Idle：其他狀態一律退化，角色只會收到自己支援的
        foreach (var state in Enum.GetValues<SpriteState>())
            rig.Window.SetState(state);
        Assert.All(rig.Block.ReceivedStates, s => Assert.Equal(SpriteState.Idle, s));
        Assert.Equal(SpriteState.Idle, rig.Window.AppliedState);
    }

    [AvaloniaFact]
    public async Task TheBubbleAndInputBar_WorkTheSameWithAnotherSprite()
    {
        var rig = CreateRig(SpriteSettings.Default with { SpriteId = BlockSpriteFactory.SpriteId });
        var submitted = new List<string>();
        rig.Window.InputSubmitted += submitted.Add;
        var bubble = rig.Window.BubbleViewModel;

        bubble.OnRunStarted();
        bubble.OnEvent(new Lumi.Core.Chat.TextDelta("你好"));
        rig.Window.Poll();
        Assert.Equal(1, rig.Window.FindControl<SpriteBubbleView>("Bubble")!.Opacity);

        rig.Window.FindControl<TextBox>("Input")!.Text = "嗨";
        rig.Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["嗨"], submitted);

        await rig.Window.SetSizeAsync(SpriteSize.Large);   // 尺寸檔位對任何角色都一樣
        Assert.Equal(SpriteSize.Large, rig.Catalog.Current.Sprite.Size);
        Assert.Equal(SpriteLimits.Fit(rig.Block.Manifest, 104).Options.Outer.Width, rig.Window.View.Visual.Width);
    }

    [AvaloniaFact]
    public void ChoosingAnotherSpriteInTheSettings_SwapsItLiveAndKeepsTheCenter()
    {
        var rig = CreateRig();
        rig.Window.Show();
        var center = rig.Window.SpriteCenterOnScreen;
        var orbVisual = rig.Window.View.Visual;
        Assert.IsNotType<Border>(orbVisual);

        rig.Catalog.SetSpriteAsync(rig.Catalog.Current.Sprite with { SpriteId = BlockSpriteFactory.SpriteId }).GetAwaiter().GetResult();

        Assert.IsType<Border>(rig.Window.View.Visual);
        Assert.Equal(center, rig.Window.SpriteCenterOnScreen);   // 換角色位置不跳動
        Assert.NotSame(orbVisual, rig.Window.View.Visual);

        rig.Catalog.SetSpriteAsync(rig.Catalog.Current.Sprite with { SpriteId = OrbSpriteFactory.SpriteId }).GetAwaiter().GetResult();
        Assert.IsNotType<Border>(rig.Window.View.Visual);
        Assert.Equal(center, rig.Window.SpriteCenterOnScreen);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void OtherSettingsChanges_DoNotRebuildTheSprite()
    {
        var rig = CreateRig();
        var visual = rig.Window.View.Visual;

        rig.Catalog.SetPersonaAsync(PersonaSettings.Default with { Style = PersonaStyle.Calm }).GetAwaiter().GetResult();
        rig.Catalog.SetSpriteAsync(rig.Catalog.Current.Sprite with { VoiceEnabled = false }).GetAwaiter().GetResult();

        Assert.Same(visual, rig.Window.View.Visual);
    }

    // ---- 設定頁的預覽 ----

    [AvaloniaFact]
    public void TheSettingsPreview_PlaysTheChosenSprite_AndShowsTheFallbackForUnsupportedStates()
    {
        using var host = new SettingsHost(null, new BlockSpriteFactory());
        var window = new SettingsWindow(host.Settings);
        window.Show();
        host.Settings.CategoryIndex = 3;
        var preview = window.Preview!;
        Assert.NotNull(preview.View);

        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == BlockSpriteFactory.SpriteId);
        host.Sprite.PreviewState = host.Sprite.States.Single(s => s.State == SpriteState.Tool);

        Assert.IsType<Border>(window.FindControl<Border>("SpritePreviewHost")!.Child);   // 預覽區換成這個角色的畫面
        Assert.Equal(SpriteState.Idle, preview.AppliedState);                           // Tool 退化成 Idle
        host.Sprite.PreviewState = host.Sprite.States.Single(s => s.State == SpriteState.Idle);
        Assert.Equal(SpriteState.Idle, preview.AppliedState);
        window.Close();
    }

    [AvaloniaFact]
    public void TheSettingsPreview_PlaysEveryStateOfTheOrb()
    {
        using var host = new SettingsHost();
        var window = new SettingsWindow(host.Settings);
        window.Show();
        var preview = window.Preview!;

        foreach (var item in host.Sprite.States)
        {
            host.Sprite.PreviewState = item;
            Assert.Equal(item.State, preview.AppliedState);   // 光球每個狀態都有自己的表現
        }
        window.Close();
    }

    [AvaloniaFact]
    public void TheSpriteCategory_IsListedInTheSettingsWindow()
    {
        using var host = new SettingsHost();
        var window = new SettingsWindow(host.Settings);
        window.Show();

        host.Settings.CategoryIndex = 3;
        window.UpdateLayout();

        Assert.NotNull(window.FindControl<ListBox>("SpriteList"));
        Assert.NotNull(window.FindControl<ComboBox>("SpriteSizeBox"));
        Assert.NotNull(window.FindControl<CheckBox>("SpriteVoiceBox"));
        Assert.Equal(10, window.FindControl<ListBox>("SpriteStateList")!.ItemCount);
        window.Close();
    }
}
