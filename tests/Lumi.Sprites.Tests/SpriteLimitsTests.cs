using Avalonia;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Sprites.Tests;

public class SpriteLimitsTests
{
    private static SpriteManifest WithBox(double width, double height, double margin = 0.2) =>
        SpriteStateResolverTests.Manifest(box: new Size(width, height), margin: margin);

    [Theory]
    [InlineData(64)]
    [InlineData(80)]
    [InlineData(104)]
    public void TheOrb_FitsEveryTierWithoutAdjustment(double tier)
    {
        var fit = SpriteLimits.Fit(new OrbSpriteFactory().Manifest, tier);

        Assert.False(fit.WasAdjusted);
        Assert.Equal(1, fit.Scale);
        Assert.Equal(tier * 1.2, fit.Options.Box.Width, 6);
        Assert.Equal(fit.Options.Box.Width, fit.Options.Box.Height, 6);
        Assert.Equal(tier, fit.Options.Diameter);
    }

    [Fact]
    public void TierScalesTheBoxProportionally()
    {
        var manifest = WithBox(100, 60);

        var small = SpriteLimits.Fit(manifest, 64);
        var large = SpriteLimits.Fit(manifest, 104);

        Assert.Equal(80, small.Options.Box.Width, 6);    // 100 × 64/80
        Assert.Equal(48, small.Options.Box.Height, 6);
        Assert.Equal(130, large.Options.Box.Width, 6);   // 100 × 104/80；仍在 104 × 1.6 = 166.4 之內
        Assert.Equal(78, large.Options.Box.Height, 6);
        Assert.False(small.WasAdjusted);
        Assert.False(large.WasAdjusted);
    }

    [Fact]
    public void LongestSideAboveTheCap_IsScaledDownEvenly_AndWarns()
    {
        var fit = SpriteLimits.Fit(WithBox(160, 100), 80);   // 上限 128

        Assert.Equal(128, fit.Options.Box.Width, 6);
        Assert.Equal(80, fit.Options.Box.Height, 6);         // 長寬比不變
        Assert.Equal(0.8, fit.Scale, 6);
        Assert.Contains(fit.Warnings, w => w.Contains("最長邊"));
    }

    [Fact]
    public void LongestSideExactlyAtTheCap_IsLeftAlone()
    {
        var fit = SpriteLimits.Fit(WithBox(128, 100), 80);

        Assert.False(fit.WasAdjusted);
        Assert.Equal(new Size(128, 100), fit.Options.Box);
    }

    [Fact]
    public void TooWide_IsNarrowedToTwoToOne()
    {
        var fit = SpriteLimits.Fit(WithBox(120, 40), 80);   // 3:1

        Assert.Equal(80, fit.Options.Box.Width, 6);          // 40 × 2
        Assert.Equal(40, fit.Options.Box.Height, 6);
        Assert.Contains(fit.Warnings, w => w.Contains("長寬比"));
    }

    [Fact]
    public void TooTall_IsShortenedToOneToTwo()
    {
        var fit = SpriteLimits.Fit(WithBox(30, 100), 80);   // 1:3.3

        Assert.Equal(30, fit.Options.Box.Width, 6);
        Assert.Equal(60, fit.Options.Box.Height, 6);        // 30 ÷ 0.5
        Assert.Contains(fit.Warnings, w => w.Contains("長寬比"));
    }

    [Fact]
    public void AspectAtTheBoundaries_IsAccepted()
    {
        Assert.False(SpriteLimits.Fit(WithBox(100, 50), 80).WasAdjusted);   // 2:1
        Assert.False(SpriteLimits.Fit(WithBox(50, 100), 80).WasAdjusted);   // 1:2
    }

    [Fact]
    public void EffectMarginAboveTheLimit_IsClamped_AndOuterSizeFollows()
    {
        var fit = SpriteLimits.Fit(WithBox(80, 80, margin: 0.9), 80);

        Assert.Contains(fit.Warnings, w => w.Contains("特效外擴"));
        Assert.Equal(80 * (1 + 2 * SpriteLimits.EffectMarginMax), fit.Options.Outer.Width, 6);
    }

    [Fact]
    public void NegativeEffectMargin_IsClampedToZero()
    {
        var fit = SpriteLimits.Fit(WithBox(80, 80, margin: -1), 80);

        Assert.Equal(fit.Options.Box, fit.Options.Outer);
        Assert.True(fit.WasAdjusted);
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(80, 0)]
    [InlineData(-5, 40)]
    public void InvalidBox_FallsBackToASquareOfTheTierDiameter(double width, double height)
    {
        var fit = SpriteLimits.Fit(WithBox(width, height), 80);

        Assert.Equal(new Size(80, 80), fit.Options.Box);
        Assert.True(fit.WasAdjusted);
    }

    [Fact]
    public void OuterSize_CentersTheBox()
    {
        var fit = SpriteLimits.Fit(WithBox(80, 60, margin: 0.25), 80);

        Assert.Equal(new Size(120, 90), fit.Options.Outer);
        Assert.Equal(new Rect(20, 15, 80, 60), fit.Options.BoxRect);
    }

    [Fact]
    public void Constants_MatchTheDevGuide()
    {
        Assert.Equal([64.0, 80.0, 104.0], SpriteLimits.Tiers);
        Assert.Equal(80, SpriteLimits.DefaultTier);
        Assert.Equal(0.5, SpriteLimits.AspectMin);
        Assert.Equal(2.0, SpriteLimits.AspectMax);
        Assert.Equal(1.6, SpriteLimits.LongSideFactor);
        Assert.Equal(0.4, SpriteLimits.EffectMarginMax);
        Assert.Equal(200, SpriteLimits.VoiceMaxLength);
        Assert.Equal("1.0", SpriteAbstractionsVersion.Current);
    }

    // ---- 目錄與註冊 ----

    [Fact]
    public void AddSprite_RegistersTheFactory_AndTheCatalogFindsIt()
    {
        var services = new ServiceCollection().AddSprite<OrbSpriteFactory>().BuildServiceProvider();

        var catalog = services.GetRequiredService<ISpriteCatalog>();

        Assert.Equal([OrbSpriteFactory.SpriteId], catalog.All.Select(f => f.Manifest.Id));
        Assert.IsType<OrbSpriteFactory>(catalog.Find("lumi.orb"));
        Assert.Null(catalog.Find("nope"));
    }

    [Fact]
    public void AddSprite_CanRegisterSeveralSprites_WithOneCatalog()
    {
        var services = new ServiceCollection().AddSprite<OrbSpriteFactory>().AddSprite<OtherFactory>().BuildServiceProvider();

        Assert.Equal(["lumi.orb", "test.other"], services.GetRequiredService<ISpriteCatalog>().All.Select(f => f.Manifest.Id).Order());
    }

    private sealed class OtherFactory : ISpriteFactory
    {
        public SpriteManifest Manifest { get; } = SpriteStateResolverTests.Manifest(id: "test.other");

        public ISpriteView Create(SpriteOptions options) => throw new NotSupportedException();
    }
}
