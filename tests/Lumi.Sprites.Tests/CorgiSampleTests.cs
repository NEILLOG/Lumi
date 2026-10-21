using Avalonia;
using Avalonia.Headless.XUnit;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;
using Lumi.Sprites.Tests.TestSupport;

namespace Lumi.Sprites.Tests;

/// <summary>
/// 真的素材包（<c>samples/sprites/corgi-pixel</c>，使用者做的像素柯基）：載入要零警告。
/// 規格對得上、美術合規（格子對、沒有貼邊被切掉）都靠這個測試守住。
/// </summary>
public class CorgiSampleTests
{
    private static string SamplesRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumi.sln")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("找不到 Lumi.sln"), "samples", "sprites");
        }
    }

    private static string CorgiDirectory => Path.Combine(SamplesRoot, "corgi-pixel");

    [Fact]
    public void TheCorgi_Loads_WithNoIssuesAtAll()
    {
        var result = AssetSpriteLoader.Load(CorgiDirectory);

        Assert.Empty(result.Issues);
        Assert.NotNull(result.Definition);
    }

    [Fact]
    public void TheCorgi_Manifest_IsAsDesigned()
    {
        var manifest = AssetSpriteLoader.Load(CorgiDirectory).Definition!.Manifest;

        Assert.Equal("neillog.corgi-pixel", manifest.Id);
        Assert.Equal("柯基（像素）", manifest.Name);
        Assert.Equal("張仲毅", manifest.Author);
        Assert.Equal(new Avalonia.Size(112, 112), manifest.Box);
        Assert.Equal(0, manifest.EffectMargin);
        Assert.Equal(SpriteHitShape.Alpha, manifest.HitShape);
        Assert.Equal(SpriteRendering.Pixelated, manifest.Rendering);
        Assert.Equal(28, manifest.PixelGrid);
        Assert.Equal(new Avalonia.Point(0.5, 0.12), manifest.BubbleAnchor);
        Assert.Equal("句尾偶爾加「汪」，語氣熱情、容易興奮，被稱讚時特別開心。", manifest.Voice);
    }

    [Fact]
    public void TheCorgi_HasAnAnimationForEveryStateAndEveryInteraction()
    {
        var definition = AssetSpriteLoader.Load(CorgiDirectory).Definition!;

        Assert.Equal(Enum.GetValues<SpriteState>().Order(), definition.Manifest.Supported.Order());   // 十個狀態全有，不需要 fallback
        Assert.Empty(definition.Manifest.Fallback);
        foreach (var key in AssetSpriteLoader.InteractionKeys)
            Assert.True(definition.Animations.ContainsKey(key), key);
        Assert.Equal(13, definition.Animations.Count);
    }

    [Fact]
    public void TheCorgi_EveryAnimationUsesTheSame280PixelCell_On_A10PixelLogicalGrid()
    {
        var definition = AssetSpriteLoader.Load(CorgiDirectory).Definition!;

        Assert.Equal(new Avalonia.Size(280, 280), definition.CellPixels);
        Assert.All(definition.Animations.Values, a => Assert.Equal(new Avalonia.Size(280, 280), a.CellPixels));
        Assert.Equal(10, 280 / definition.Manifest.PixelGrid!.Value);   // 28 個邏輯像素，每個 10 px
    }

    [Theory]
    [InlineData("idle", 12, 4, 8, true, null, false)]
    [InlineData("done", 8, 4, 12, false, "idle", false)]
    [InlineData("error", 8, 4, 10, false, null, true)]
    [InlineData("poke.press", 4, 4, 16, false, null, true)]
    [InlineData("poke.release", 8, 4, 16, false, "idle", false)]
    [InlineData("drag", 6, 6, 12, true, null, false)]
    [InlineData("sleep", 8, 4, 4, true, null, false)]
    public void TheCorgi_AnimationsKeepTheirTiming(string key, int frames, int columns, double fps, bool loop, string? next, bool hold)
    {
        var animation = AssetSpriteLoader.Load(CorgiDirectory).Definition!.Animations[key];

        Assert.Equal((frames, columns, fps, loop, next, hold), (animation.Frames, animation.Columns, animation.Fps, animation.Loop, animation.Next, animation.HoldLast));
    }

    [Fact]
    public void TheCorgi_HasAPreview()
    {
        var definition = AssetSpriteLoader.Load(CorgiDirectory).Definition!;

        Assert.NotNull(definition.PreviewPath);
        Assert.True(File.Exists(definition.PreviewPath));
    }

    [Fact]
    public void TheCorgi_FitsEveryTier_WithoutAnyAdjustment()
    {
        var manifest = AssetSpriteLoader.Load(CorgiDirectory).Definition!.Manifest;

        foreach (var tier in SpriteLimits.Tiers)
            Assert.False(SpriteLimits.Fit(manifest, tier).WasAdjusted, $"tier {tier}");
    }

    [Fact]
    public void TheSamplesFolder_AsAWhole_Scans_WithNoIssues()
    {
        var result = AssetSpriteScanner.Scan(SamplesRoot, new HashSet<string> { "lumi.orb" });

        Assert.Empty(result.Issues);
        Assert.Contains(result.Sprites, s => s.Manifest.Id == "neillog.corgi-pixel");
    }

    [Fact]
    public void TheCorgi_VoiceIsAccepted_ByTheSharedValidator()
    {
        var voice = AssetSpriteLoader.Load(CorgiDirectory).Definition!.Manifest.Voice;

        Assert.False(SpriteVoiceValidator.Check(voice).IsRejected);
    }

    // ---- 真的畫出來（Skia） ----

    private static (AssetSpriteFactory Factory, ISpriteView View, SpriteOptions Options) Corgi(double tier = 80)
    {
        var factory = new AssetSpriteFactory(AssetSpriteLoader.Load(CorgiDirectory).Definition!);
        var options = SpriteLimits.Fit(factory.Manifest, tier).Options;
        return (factory, factory.Create(options), options);
    }

    [AvaloniaFact]
    public void TheCorgi_EveryStateAndInteraction_Paints_AndNothingIsCutOffByTheCellEdges()
    {
        var (factory, view, options) = Corgi();
        var frame = TimeSpan.FromMilliseconds(130);

        foreach (var state in Enum.GetValues<SpriteState>())
        {
            view.SetState(state);
            for (var i = 0; i < 14; i++)   // 走遍每個動畫的每一格
                view.Advance(frame);
            Assert.Contains(Snapshot.Take(view.Visual, options.Outer).AllPixels(), p => p >> 24 != 0);
        }
        view.OnInteraction(new SpriteInteraction.PokeBegin(options.BoxRect.Center));
        Assert.Contains(Snapshot.Take(view.Visual, options.Outer).AllPixels(), p => p >> 24 != 0);
        view.OnInteraction(new SpriteInteraction.PokeEnd());
        view.OnInteraction(new SpriteInteraction.DragBegin());
        Assert.Contains(Snapshot.Take(view.Visual, options.Outer).AllPixels(), p => p >> 24 != 0);

        // 解碼過所有動畫之後，還是沒有任何警告：貼邊的問題在素材修好之後不再出現
        Assert.Empty(factory.Issues);
        Assert.Equal(13, factory.Definition.Animations.Count);
    }

    [AvaloniaTheory]
    [InlineData(80, 1.0, 4)]    // 中檔、1 倍：每個邏輯像素 4 px（整張 112 px）
    [InlineData(80, 2.0, 8)]    // 中檔、2 倍：8 px
    [InlineData(64, 1.0, 3)]    // 小檔：89.6 → 3 px 一個（84 px）
    [InlineData(104, 1.0, 5)]   // 大檔：145.6 → 5 px 一個（140 px）
    [InlineData(64, 2.0, 6)]
    [InlineData(104, 2.0, 10)]
    public void TheCorgi_EveryLogicalPixelIsAWholeNumberOfDevicePixels_OnEveryTierAndScale(double tier, double scaling, int perPixel)
    {
        var (_, view, options) = Corgi(tier);
        ((SpriteSheetControl)view.Visual).RenderScalingOverride = scaling;

        var snapshot = Snapshot.Take(view.Visual, options.Outer, scaling);

        // 隨便挑幾列：有顏色的連續段落，長度都必須是「每個邏輯像素的裝置像素數」的整數倍（沒有 5px、6px 交錯的不均勻）；透明的留白不算
        foreach (var fraction in new[] { 0.4, 0.55, 0.7, 0.85 })
        {
            var y = (int)(snapshot.Height * fraction);
            Assert.All(snapshot.OpaqueRuns(y, 0, snapshot.Width), run => Assert.Equal(0, run % perPixel));
        }
    }

    [AvaloniaFact]
    public void TheCorgi_StandsOnTheBottomOfItsBox_AtTheMiddleTier()
    {
        var (_, view, options) = Corgi();

        var snapshot = Snapshot.Take(view.Visual, options.Outer);

        Assert.False(snapshot.IsTransparent((int)(snapshot.Width * 0.5), snapshot.Height - 1));    // 腳底貼著底邊
        Assert.True(snapshot.IsTransparent(2, 2));                                                  // 左上角是透明的
    }

    [AvaloniaFact]
    public void TheCorgi_HitTest_FollowsItsOutline()
    {
        var (_, view, options) = Corgi();
        // 柯基的格子 280×280 對到框 112×112（0.4 倍）：身體在 x 50～230、y 50～280
        Point At(double cellX, double cellY) => new(options.BoxRect.X + cellX * 0.4, options.BoxRect.Y + cellY * 0.4);

        Assert.True(view.HitTest(At(140, 170)));      // 胸前
        Assert.True(view.HitTest(At(100, 80)));       // 頭
        Assert.True(view.HitTest(At(60, 270)));       // 左腳：橢圓會漏掉
        Assert.True(view.HitTest(At(220, 270)));      // 右腳
        Assert.False(view.HitTest(At(10, 10)));       // 左上角的透明處
        Assert.False(view.HitTest(At(270, 20)));
        Assert.False(view.HitTest(At(270, 270)));     // 右下角（身體旁邊）
        Assert.False(view.HitTest(new Point(options.BoxRect.Right + 2, options.BoxRect.Center.Y)));   // 框外
    }

    [AvaloniaFact]
    public void TheCorgi_BubbleAnchor_IsAboveItsEars()
    {
        var (_, view, options) = Corgi();

        // y = 0.12 × 框高；耳朵尖在格子 y=50（0.18）
        Assert.Equal(options.BoxRect.Y + 0.12 * options.BoxRect.Height, view.BubbleAnchor.Y, 6);
        Assert.True(view.BubbleAnchor.Y < options.BoxRect.Y + 50 * 0.4);
        Assert.Equal(options.BoxRect.Center.X, view.BubbleAnchor.X, 6);
    }

    [AvaloniaFact]
    public void TheCorgi_IsMuchCheaperThanTheOrb_Idle8Fps_Sleep4Fps()
    {
        var (_, view, _) = Corgi();

        Assert.Equal(8, view.PreferredFps);
        view.SetState(SpriteState.Sleep);
        Assert.Equal(4, view.PreferredFps);
        view.SetState(SpriteState.Error);
        for (var i = 0; i < 20; i++)
            view.Advance(TimeSpan.FromMilliseconds(120));
        Assert.Equal(1, view.PreferredFps);   // error 停在最後一格
    }
}
