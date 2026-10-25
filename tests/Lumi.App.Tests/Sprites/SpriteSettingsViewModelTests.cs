using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;

namespace Lumi.App.Tests.Sprites;

/// <summary>設定 ›「小精靈」：角色清單、尺寸、語氣、狀態預覽；選了立即生效。</summary>
public sealed class SpriteSettingsViewModelTests
{
    [Fact]
    public void Defaults_ShowTheOrb_MediumSize_VoiceOn()
    {
        using var host = new SettingsHost();

        var vm = host.Sprite;

        Assert.Equal(OrbSpriteFactory.SpriteId, vm.SelectedSprite.Id);
        Assert.Equal("光球", vm.SelectedSprite.Name);
        Assert.Equal("作者：Lumi · 版本 1.0.0", vm.SelectedSprite.Credit);
        Assert.Equal(SpriteSize.Medium, vm.SelectedSize.Size);
        Assert.True(vm.VoiceEnabled);
        Assert.Single(vm.Sprites);
        Assert.Equal(["小（64）", "中（80，預設）", "大（104）"], vm.Sizes.Select(s => s.Text));
    }

    // ---- 立即生效 ----

    [Fact]
    public async Task SelectingASize_IsSavedImmediately_AndSurvivesReload()
    {
        using var host = new SettingsHost();

        host.Sprite.SelectedSize = host.Sprite.Sizes.Single(s => s.Size == SpriteSize.Large);
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.Size == SpriteSize.Large);

        Assert.Equal(SpriteSize.Large, host.Sprite.SelectedSize.Size);
        Assert.Null(host.Sprite.Error);
    }

    [Fact]
    public async Task TheVoiceToggle_IsSavedImmediately()
    {
        using var host = new SettingsHost();

        host.Sprite.VoiceEnabled = false;
        await SettingsHost.WaitUntil(() => !host.Catalog.Current.Sprite.VoiceEnabled);

        host.Sprite.VoiceEnabled = true;
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.VoiceEnabled);
    }

    [Fact]
    public async Task ChangingASetting_KeepsThePositionAndTheLastMode()
    {
        using var host = new SettingsHost();
        await host.Catalog.SetSpriteAsync(SpriteSettings.Default with { Placement = new SpritePlacement(900, 700), LastMode = ChatMode.Sprite });

        host.Sprite.SelectedSize = host.Sprite.Sizes.Single(o => o.Size == SpriteSize.Small);
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.Size == SpriteSize.Small);

        Assert.Equal(new SpritePlacement(900, 700), host.Catalog.Current.Sprite.Placement);
        Assert.Equal(ChatMode.Sprite, host.Catalog.Current.Sprite.LastMode);
        Assert.Equal(SpriteSize.Small, host.Catalog.Current.Sprite.Size);
    }

    [Fact]
    public async Task SelectingAnotherSprite_IsSaved_AndTheStatePreviewIsRecomputed()
    {
        using var host = new SettingsHost(null, new BlockSpriteFactory());
        Assert.Equal(2, host.Sprite.Sprites.Count);
        Assert.All(host.Sprite.States, s => Assert.True(s.IsNative));   // 光球十個狀態都有自己的表現

        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == BlockSpriteFactory.SpriteId);
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.SpriteId == BlockSpriteFactory.SpriteId);

        Assert.Equal(BlockSpriteFactory.SpriteId, host.Sprite.PreviewFactory.Manifest.Id);
        var states = host.Sprite.States.ToDictionary(s => s.State);
        Assert.True(states[SpriteState.Idle].IsNative);
        Assert.Equal(Strings.SpriteSettings_PreviewNative, states[SpriteState.Idle].Note);
        Assert.False(states[SpriteState.Tool].IsNative);
        Assert.Equal("沒有專屬表現，改用「待機」", states[SpriteState.Tool].Note);        // Tool → Thinking → Idle，一路退到底
        Assert.Equal("沒有專屬表現，改用「待機」", states[SpriteState.Transcribing].Note);   // Transcribing → Listening → Idle
        Assert.Equal(SpriteState.Idle, states[SpriteState.Attention].Resolved);
    }

    [Fact]
    public void TheStateList_HasAllTenStates_InOrder_WithLabels()
    {
        using var host = new SettingsHost();

        Assert.Equal(
            ["待機", "聆聽", "轉寫", "思考", "呼叫工具", "回覆中", "完成", "需要確認", "錯誤", "休眠"],
            host.Sprite.States.Select(s => s.Label));
        Assert.Equal(SpriteState.Idle, host.Sprite.PreviewState!.State);
    }

    [Fact]
    public void TheVoiceNote_TellsWhenTheCharacterHasNoVoice()
    {
        using var host = new SettingsHost(null, new BlockSpriteFactory());
        Assert.False(host.Sprite.HasVoice);   // 光球沒有語氣
        Assert.True(host.Sprite.HasNoVoice);

        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == BlockSpriteFactory.SpriteId);

        Assert.True(host.Sprite.HasVoice);
        Assert.False(host.Sprite.HasNoVoice);
    }

    [Fact]
    public async Task ThePreviewState_SurvivesSwitchingCharacters()
    {
        using var host = new SettingsHost(null, new BlockSpriteFactory());
        host.Sprite.PreviewState = host.Sprite.States.Single(s => s.State == SpriteState.Thinking);

        host.Sprite.SelectedSprite = host.Sprite.Sprites.Single(s => s.Id == BlockSpriteFactory.SpriteId);
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Sprite.SpriteId == BlockSpriteFactory.SpriteId);

        Assert.Equal(SpriteState.Thinking, host.Sprite.PreviewState!.State);
        Assert.Equal(SpriteState.Idle, host.Sprite.PreviewState.Resolved);   // 還是同一個狀態，只是現在會退化
    }

    // ---- 設定變了 ----

    [Fact]
    public async Task ASavedSpriteThatIsNoLongerInstalled_FallsBackToTheFirst()
    {
        using var host = new SettingsHost();
        await host.Catalog.SetSpriteAsync(SpriteSettings.Default with { SpriteId = "gone.sprite" });

        var vm = new SpriteSettingsViewModel(host.Catalog, host.SpriteCatalog, new SyncUiDispatcher());

        Assert.Equal(OrbSpriteFactory.SpriteId, vm.SelectedSprite.Id);
    }

    [Fact]
    public async Task ChangesMadeElsewhere_AreShown()
    {
        using var host = new SettingsHost();

        await host.Catalog.SetSpriteAsync(SpriteSettings.Default with { Size = SpriteSize.Small, VoiceEnabled = false });

        Assert.Equal(SpriteSize.Small, host.Sprite.SelectedSize.Size);
        Assert.False(host.Sprite.VoiceEnabled);
    }

    [Fact]
    public async Task ASaveThatFails_ShowsAnError_AndGoesBackToTheEffectiveValue()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);

        host.Sprite.SelectedSize = host.Sprite.Sizes.Single(s => s.Size == SpriteSize.Large);
        await SettingsHost.WaitUntil(() => host.Sprite.Error is not null);

        Assert.Equal(SpriteSize.Medium, host.Sprite.SelectedSize.Size);   // 存不起來，畫面回到實際的值
    }

    // ---- 設定視窗的分類 ----

    [Fact]
    public async Task TheSettingsWindow_HasAFourthCategory_ForTheSprite()
    {
        using var host = new SettingsHost();

        await host.Settings.NavigateAsync(new SettingsNavigation(SettingsSection.Sprite));

        Assert.True(host.Settings.ShowSprite);
        Assert.Equal(5, host.Settings.CategoryIndex);
        Assert.False(host.Settings.ShowGeneral);
        Assert.False(host.Settings.ShowPersona);
        Assert.False(host.Settings.ShowConnectionsAndModels);
        Assert.False(host.Settings.HasUnsavedChanges);   // 立即生效，沒有草稿
    }
}
