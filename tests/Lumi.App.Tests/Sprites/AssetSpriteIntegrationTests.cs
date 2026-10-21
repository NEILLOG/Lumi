using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Models;
using Lumi.Core.Platform;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>
/// 素材型角色接上殼與設定頁（真的 <see cref="SpriteLibrary"/>、真的柯基範例、headless 視窗）：
/// 載入、選用、重新載入、角色消失與回來，殼都不需要認識它是素材。
/// </summary>
public class AssetSpriteIntegrationTests
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

    private static SpriteLibrary NewLibrary(CorgiPack pack) => new([new OrbSpriteFactory()], new AssetSpriteOptions { Directory = pack.Root });

    private static SpriteWindow NewWindow(SpriteLibrary library, FakeModelCatalog catalog) =>
        new(library, new QuietOverlay(), catalog, new SpriteShellOptions(), new FakeTimeProvider(), NullLogger<SpriteWindow>.Instance);

    private static bool IsOrb(SpriteWindow window) => window.View.GetType().Name == "OrbSpriteView";

    private static bool IsAsset(SpriteWindow window) => window.View.GetType().Name == "AssetSpriteView";

    private static FakeModelCatalog CatalogWith(string spriteId, SpriteSize size = SpriteSize.Medium)
    {
        var catalog = new FakeModelCatalog();
        catalog.Current = catalog.Current with { Sprite = SpriteSettings.Default with { SpriteId = spriteId, Size = size } };
        return catalog;
    }

    // ---- 殼 ----

    [AvaloniaFact]
    public async Task TheShell_StartsWithTheSavedAssetSprite_WhenTheLibraryWasLoadedFirst()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();   // 啟動時先載入角色資料夾，再建立小精靈視窗

        var window = NewWindow(library, CatalogWith(CorgiPack.CorgiId));

        Assert.True(IsAsset(window));
        var options = SpriteLimits.Fit(library.Find(CorgiPack.CorgiId)!.Manifest, SpriteLimits.DefaultTier).Options;
        Assert.Equal(options.Outer.Width, window.View.Visual.Width);
        Assert.Equal(SpriteState.Idle, window.AppliedState);
    }

    [AvaloniaFact]
    public async Task TheShell_PlaysAnAssetSpriteThroughTheSameStatesAndInteractions()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        var window = NewWindow(library, CatalogWith(CorgiPack.CorgiId));

        foreach (var state in Enum.GetValues<SpriteState>())
        {
            window.SetState(state);
            Assert.Equal(state, window.AppliedState);   // 柯基十個狀態都有自己的動畫，不需要退化
        }
        window.SetState(SpriteState.Thinking);
        Assert.Equal(8, window.View.PreferredFps);       // 動畫自己的 fps
        window.View.OnInteraction(new SpriteInteraction.PokeBegin(window.View.BubbleAnchor));
        window.View.OnInteraction(new SpriteInteraction.PokeEnd());
        Assert.Equal(16, window.View.PreferredFps);      // poke.release 的 fps
    }

    [AvaloniaFact]
    public async Task WhenTheLibraryIsReloadedAfterTheShellStarted_TheSavedSpriteIsSwappedIn_WithoutMovingIt()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        var window = NewWindow(library, CatalogWith(CorgiPack.CorgiId));
        window.Show();
        Assert.True(IsOrb(window));                      // 還沒載入：先用內建的光球
        var center = window.SpriteCenterOnScreen;

        await library.ReloadAsync();

        Assert.True(IsAsset(window));
        Assert.Equal(center, window.SpriteCenterOnScreen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WhenTheSelectedSpriteDisappears_TheShellFallsBackToTheOrb_AndTheSettingsKeepTheChoice()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        var catalog = CatalogWith(CorgiPack.CorgiId);
        var window = NewWindow(library, catalog);
        window.Show();
        var center = window.SpriteCenterOnScreen;
        Assert.True(IsAsset(window));

        pack.Uninstall();
        await library.ReloadAsync();

        Assert.True(IsOrb(window));
        Assert.Equal(center, window.SpriteCenterOnScreen);
        Assert.Equal(CorgiPack.CorgiId, catalog.Current.Sprite.SpriteId);   // 設定裡的選擇不動

        pack.Install();
        await library.ReloadAsync();

        Assert.True(IsAsset(window));   // 角色回來，重新載入就自動換回去
        Assert.Equal(center, window.SpriteCenterOnScreen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReloadingTheSameSprite_RebuildsIt_SoEditedArtIsPickedUp()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        var window = NewWindow(library, CatalogWith(CorgiPack.CorgiId));
        var before = window.View;

        await library.ReloadAsync();

        Assert.NotSame(before, window.View);
        Assert.True(IsAsset(window));
    }

    [AvaloniaFact]
    public async Task SwitchingBetweenTheOrbAndAnAssetSprite_FromTheSettings_Works_AndKeepsTheSize()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        var catalog = CatalogWith(OrbSpriteFactory.SpriteId, SpriteSize.Large);
        var window = NewWindow(library, catalog);
        Assert.True(IsOrb(window));

        await catalog.SetSpriteAsync(catalog.Current.Sprite with { SpriteId = CorgiPack.CorgiId });
        Assert.True(IsAsset(window));
        Assert.Equal(SpriteLimits.Fit(library.Find(CorgiPack.CorgiId)!.Manifest, 104).Options.Outer.Width, window.View.Visual.Width);

        await catalog.SetSpriteAsync(catalog.Current.Sprite with { SpriteId = OrbSpriteFactory.SpriteId });
        Assert.True(IsOrb(window));
    }

    [AvaloniaFact]
    public async Task AnAssetSpriteInTheShell_UsesTheBubbleAnchorOfItsManifest()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        var window = NewWindow(library, CatalogWith(CorgiPack.CorgiId));

        // 柯基的 bubbleAnchor y = 0.12：氣泡的尖端落在角色框上方（耳朵之上）
        var box = window.Layout.SpriteBox;
        Assert.Equal(window.Layout.BubbleBottom, window.Layout.SpriteOrigin.Y + window.View.BubbleAnchor.Y, 6);
        Assert.True(window.Layout.BubbleBottom < box.Y + box.Height * 0.2);
    }

    // ---- 設定頁 ----

    [Fact]
    public async Task TheSettingsList_ShowsTheOrbAndTheCorgi_WithKindAndThumbnail()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);

        var sprites = host.Sprite.Sprites;

        Assert.Equal(["lumi.orb", CorgiPack.CorgiId], sprites.Select(s => s.Id));
        Assert.Equal(["程式型", "素材型"], sprites.Select(s => s.KindText));
        Assert.Null(sprites[0].PreviewPath);
        Assert.EndsWith("preview.png", sprites[1].PreviewPath);
        Assert.True(File.Exists(sprites[1].PreviewPath));
        Assert.Equal("作者：張仲毅 · 版本 0.1.0", sprites[1].Credit);
        Assert.True(host.Sprite.CanReload);
        Assert.Equal(pack.Root, host.Sprite.SpritesDirectory);
    }

    [Fact]
    public async Task Reload_FromTheSettings_PicksUpANewSprite_AndRebuildsTheList()
    {
        using var pack = new CorgiPack();
        pack.Uninstall();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);
        Assert.Single(host.Sprite.Sprites);

        pack.Install();
        await host.Sprite.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(2, host.Sprite.Sprites.Count);
        Assert.False(host.Sprite.IsReloading);
        Assert.Equal("重新載入", host.Sprite.ReloadLabel);
    }

    [Fact]
    public async Task WhileReloading_TheButtonIsDisabled_AndSaysSo()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        using var host = new SettingsHost(library);
        var states = new List<(bool Reloading, bool CanExecute, string Label)>();
        library.Changed += (_, _) => states.Add((host.Sprite.IsReloading, host.Sprite.ReloadCommand.CanExecute(null), host.Sprite.ReloadLabel));

        await host.Sprite.ReloadCommand.ExecuteAsync(null);

        var during = Assert.Single(states);
        Assert.True(during.Reloading);
        Assert.False(during.CanExecute);
        Assert.Equal("載入中…", during.Label);
        Assert.True(host.Sprite.ReloadCommand.CanExecute(null));   // 結束後又可以按
    }

    [Fact]
    public async Task BrokenSprites_AreListedAsIssues_AndLoadedOnesWithWarningsToo()
    {
        using var pack = new CorgiPack();
        pack.AddBroken();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);

        var issues = host.Sprite.Issues;

        Assert.True(host.Sprite.HasIssues);
        var item = Assert.Single(issues);
        Assert.True(item.IsFatal);
        Assert.Equal(Lumi.App.Resources.Strings.SpriteSettings_Issue_Fatal, item.KindText);
        Assert.Contains("broken", item.Text);
        Assert.Contains("animations", item.Text);
        Assert.Equal(["lumi.orb", CorgiPack.CorgiId], host.Sprite.Sprites.Select(s => s.Id));   // 壞掉的不在清單裡
    }

    [Fact]
    public async Task ACleanLibrary_HasNoIssues()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);

        Assert.False(host.Sprite.HasIssues);
        Assert.Empty(host.Sprite.Issues);
    }

    [Fact]
    public async Task FixingTheBrokenSprite_AndReloading_ClearsTheIssue()
    {
        using var pack = new CorgiPack();
        pack.AddBroken();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);
        Assert.True(host.Sprite.HasIssues);

        Directory.Delete(Path.Combine(pack.Root, "broken"), recursive: true);
        await host.Sprite.ReloadCommand.ExecuteAsync(null);

        Assert.False(host.Sprite.HasIssues);
    }

    [Fact]
    public async Task ASavedSpriteThatWentAway_ShowsTheFirstOne_WithoutChangingTheSettings_AndPickingItSavesIt()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);
        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == CorgiPack.CorgiId);
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.SpriteId == CorgiPack.CorgiId);

        pack.Uninstall();
        await host.Sprite.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(OrbSpriteFactory.SpriteId, host.Sprite.SelectedSprite.Id);                 // 顯示光球
        Assert.Equal(CorgiPack.CorgiId, host.Catalog.Current.Sprite.SpriteId);                  // 設定不動
        Assert.Equal(OrbSpriteFactory.SpriteId, host.Sprite.PreviewFactory.Manifest.Id);

        host.Sprite.SelectedSprite = host.Sprite.Sprites[0];                                    // 使用者明確選光球
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.SpriteId == OrbSpriteFactory.SpriteId);
    }

    [Fact]
    public async Task WhenTheSpriteComesBack_TheListSelectsItAgain_Automatically()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);
        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == CorgiPack.CorgiId);
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.SpriteId == CorgiPack.CorgiId);
        pack.Uninstall();
        await host.Sprite.ReloadCommand.ExecuteAsync(null);

        pack.Install();
        await host.Sprite.ReloadCommand.ExecuteAsync(null);

        Assert.Equal(CorgiPack.CorgiId, host.Sprite.SelectedSprite.Id);
    }

    [Fact]
    public async Task TheStatePreviewList_ForTheCorgi_HasNoFallbacks()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);

        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == CorgiPack.CorgiId);

        Assert.Equal(10, host.Sprite.States.Count);   // 互動動畫（poke.*、drag）不是狀態，不列入
        Assert.All(host.Sprite.States, s => Assert.True(s.IsNative));
        Assert.True(host.Sprite.HasVoice);            // 柯基有語氣
    }

    [Fact]
    public async Task AnAssetSpriteWithOnlyIdle_ShowsTheFallbacksInThePreviewList()
    {
        using var pack = new CorgiPack();
        // 柯基資料夾改成只留 idle：其他動畫的檔案刪掉並改 manifest
        var manifestPath = Path.Combine(pack.CorgiFolder, "manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var animations = manifest["animations"]!.AsObject();
        foreach (var key in animations.Select(p => p.Key).Where(k => k != "idle").ToList())
            animations.Remove(key);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);

        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == CorgiPack.CorgiId);

        var tool = host.Sprite.States.Single(s => s.State == SpriteState.Tool);
        Assert.False(tool.IsNative);
        Assert.Equal("沒有專屬表現，改用「待機」", tool.Note);
    }

    [Fact]
    public async Task OpenFolder_UsesTheLibraryDirectory()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        var opener = new FakeFolderOpener();
        using var host = new SettingsHost(library, opener);

        await host.Sprite.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([pack.Root], opener.Opened);
        Assert.Null(host.Sprite.Error);
    }

    [Fact]
    public async Task OpenFolder_WhenItFails_ShowsAnError()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        using var host = new SettingsHost(library, new FakeFolderOpener(succeeds: false));

        await host.Sprite.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal("無法開啟資料夾。", host.Sprite.Error);
    }

    [Fact]
    public void WithAStaticCatalog_ThereIsNothingToReload()
    {
        using var host = new SettingsHost();

        Assert.False(host.Sprite.CanReload);
        Assert.False(host.Sprite.HasSpritesDirectory);
        Assert.False(host.Sprite.ReloadCommand.CanExecute(null));
        Assert.Empty(host.Sprite.Issues);
    }

    // ---- 設定視窗 ----

    [AvaloniaFact]
    public async Task TheSettingsWindow_ShowsTheSpriteListWithThumbnails_TheButtons_AndTheIssues()
    {
        using var pack = new CorgiPack();
        pack.AddBroken();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library, new FakeFolderOpener());
        var window = new SettingsWindow(host.Settings);
        window.Show();

        host.Settings.CategoryIndex = 3;
        window.UpdateLayout();

        Assert.Equal(2, window.FindControl<ListBox>("SpriteList")!.ItemCount);
        Assert.True(window.FindControl<Button>("SpriteReloadButton")!.IsEffectivelyVisible);
        Assert.True(window.FindControl<Button>("SpriteOpenFolderButton")!.IsEffectivelyVisible);
        Assert.Equal(pack.Root, window.FindControl<TextBlock>("SpriteFolderPath")!.Text);
        Assert.True(window.FindControl<StackPanel>("SpriteIssuesPanel")!.IsVisible);
        Assert.Equal(1, window.FindControl<ItemsControl>("SpriteIssueList")!.ItemCount);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheSettingsPreview_PlaysTheCorgi_AndSwitchesStates()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);
        var window = new SettingsWindow(host.Settings);
        window.Show();
        var preview = window.Preview!;

        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == CorgiPack.CorgiId);
        foreach (var item in host.Sprite.States)
        {
            host.Sprite.PreviewState = item;
            Assert.Equal(item.State, preview.AppliedState);
        }

        Assert.Equal("AssetSpriteView", preview.View!.GetType().Name);
        // 解碼與貼邊的檢查由 Sprites.Tests 用真的 Skia 驗證（柯基零警告）；這個專案的 headless 繪製是假的（位圖都是 1×1），所以這裡不檢查
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheSettingsPreview_RebuildsAfterAReload()
    {
        using var pack = new CorgiPack();
        var library = NewLibrary(pack);
        await library.ReloadAsync();
        using var host = new SettingsHost(library);
        var window = new SettingsWindow(host.Settings);
        window.Show();
        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == CorgiPack.CorgiId);
        var before = window.Preview!.View;

        await host.Sprite.ReloadCommand.ExecuteAsync(null);

        Assert.NotSame(before, window.Preview.View);
        window.Close();
    }
}
