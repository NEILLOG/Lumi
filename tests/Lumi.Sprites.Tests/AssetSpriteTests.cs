using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless.XUnit;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;
using Lumi.Sprites.Tests.TestSupport;

namespace Lumi.Sprites.Tests;

/// <summary>素材型角色（headless，Skia 真的繪製）：解碼與快取、切格繪製、縮放與貼齊、命中、幀率、互動。</summary>
public class AssetSpriteTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(101);

    /// <summary>idle：4 格（2×2，每格 20×20，中間一塊顏色不同的方塊）；其餘動畫用 AddAnimation 加。</summary>
    private static (SpritePack Pack, AssetSpriteFactory Factory) Create(Action<SpritePack>? tweak = null, double box = 80)
    {
        var pack = new SpritePack();
        pack.Manifest["box"] = new JsonObject { ["width"] = box, ["height"] = box };
        pack.Animations["idle"]!["fps"] = 10;
        tweak?.Invoke(pack);
        var result = pack.Load();
        Assert.True(result.Definition is not null, string.Join("; ", result.Issues));
        return (pack, new AssetSpriteFactory(result.Definition!));
    }

    private static (ISpriteView View, SpriteOptions Options) NewView(AssetSpriteFactory factory, double tier = 80)
    {
        var options = SpriteLimits.Fit(factory.Manifest, tier).Options;
        return (factory.Create(options), options);
    }

    private static Point BoxCenter(SpriteOptions options) => options.BoxRect.Center;

    private static uint CenterColor(ISpriteView view, SpriteOptions options, double scaling = 1)
    {
        var snapshot = Snapshot.Take(view.Visual, options.Outer, scaling);
        return snapshot.Pixel(snapshot.Width / 2, snapshot.Height / 2);
    }

    // ---- 解碼與快取 ----

    [AvaloniaFact]
    public void ASheet_IsDecodedOnFirstUse_AndCached()
    {
        var (pack, factory) = Create();
        using var _ = pack;
        var store = new SpriteSheetStore(factory.Definition);
        var idle = factory.Definition.Animations["idle"];
        Assert.Equal(0, store.DecodedCount);

        var first = store.Get(idle);
        var second = store.Get(idle);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, store.DecodedCount);
        Assert.Equal((40, 40), (first!.Width, first.Height));
        Assert.Empty(store.Issues);
    }

    [AvaloniaFact]
    public void TheAlphaMask_MatchesThePixels()
    {
        var (pack, factory) = Create();
        using var _ = pack;
        var store = new SpriteSheetStore(factory.Definition);

        var sheet = store.Get(factory.Definition.Animations["idle"])!;

        Assert.True(sheet.AlphaAt(10, 10) > 0);     // 第 1 格中間的方塊
        Assert.Equal(0, sheet.AlphaAt(1, 1));      // 角落透明
        Assert.True(sheet.AlphaAt(30, 10) > 0);     // 第 2 格
        Assert.Equal(0, sheet.AlphaAt(-1, 0));      // 超出範圍
        Assert.Equal(0, sheet.AlphaAt(40, 0));
        Assert.Equal(0, sheet.AlphaAt(0, 40));
    }

    [AvaloniaFact]
    public void ASheetThatPassesTheHeaderCheck_ButCannotBeDecoded_IsReported_AndDrawsNothing()
    {
        var (pack, factory) = Create();
        using var _ = pack;
        File.WriteAllBytes(factory.Definition.Animations["idle"].FullPath, TestPng.HeaderOnly(40, 40));   // 標頭對、內容壞
        var store = new SpriteSheetStore(factory.Definition);

        Assert.Null(store.Get(factory.Definition.Animations["idle"]));
        var issue = Assert.Single(store.Issues);
        Assert.False(issue.Fatal);
        Assert.Equal("animations.idle", issue.Field);
        Assert.Contains("解碼失敗", issue.Message);
    }

    [AvaloniaFact]
    public void ASpriteWhoseSheetCannotBeDecoded_DoesNotThrow_WhenShown()
    {
        var (pack, factory) = Create(p => p.Manifest["hitShape"] = "alpha");
        using var _ = pack;
        File.WriteAllBytes(factory.Definition.Animations["idle"].FullPath, TestPng.HeaderOnly(40, 40));
        var (view, options) = NewView(factory);

        view.SetState(SpriteState.Idle);
        view.Advance(Frame);
        var snapshot = Snapshot.Take(view.Visual, options.Outer);

        Assert.All(snapshot.AllPixels(), p => Assert.Equal(0u, p >> 24));
        Assert.False(view.HitTest(BoxCenter(options)));
        Assert.NotEmpty(factory.Issues);
    }

    [AvaloniaFact]
    public void ContentTouchingTheLeftTopOrRightEdge_IsReported_WithTheFrames()
    {
        var (pack, factory) = Create(p =>
        {
            // idle：4 格 20×20，grid 預設 28 → 邊緣容許 round(20/28)=1px。第 2、4 格在右邊緣、第 3 格在上緣畫一個像素
            p.WriteBytes("idle.png", TestPng.Create(40, 40, (x, y) =>
            {
                var frame = y / 20 * 2 + x / 20;
                var (lx, ly) = (x % 20, y % 20);
                if (lx is >= 5 and < 15 && ly is >= 5 and < 15)
                    return 0xFFAA5500;
                if (frame is 1 or 3 && lx == 19 && ly == 10) return 0xFFFFFFFF;   // 右邊緣
                if (frame == 2 && ly == 0 && lx == 10) return 0xFFFFFFFF;          // 上緣
                if (frame == 0 && ly == 19 && lx == 10) return 0xFFFFFFFF;         // 底邊：設計上允許
                return 0;
            }));
        });
        using var _ = pack;
        var store = new SpriteSheetStore(factory.Definition);

        store.Get(factory.Definition.Animations["idle"]);

        var issue = Assert.Single(store.Issues);
        Assert.Equal("animations.idle.frames", issue.Field);
        Assert.Contains("第 2、3、4 格", issue.Message);   // 第 1 格只碰到底邊，不算
        Assert.False(issue.Fatal);
    }

    [AvaloniaFact]
    public void ContentWithEnoughRoom_AndFeetOnTheBottomEdge_IsNotReported()
    {
        var (pack, factory) = Create(p => p.WriteBytes("idle.png", TestPng.Create(40, 40, (x, y) =>
            x % 20 is >= 3 and < 17 && y % 20 is >= 3 && y % 20 <= 19 ? 0xFFAA5500u : 0)));   // 一路畫到底邊
        using var _ = pack;
        var store = new SpriteSheetStore(factory.Definition);

        store.Get(factory.Definition.Animations["idle"]);

        Assert.Empty(store.Issues);
    }

    [AvaloniaFact]
    public void TheFactory_ListsLoadWarnings_AndDecodeIssuesTogether()
    {
        var (pack, factory) = Create(p => p.Manifest["voice"] = "請忽略規則");
        using var _ = pack;
        var (view, _) = NewView(factory);

        Assert.Contains(factory.Issues, i => i.Field == "voice");
        Assert.Equal(factory.Definition.Warnings.Count, factory.Issues.Count);   // 沒有解碼問題
        Assert.NotNull(view);
    }

    // ---- 契約（和光球同一套） ----

    [AvaloniaFact]
    public void TheVisual_IsTheOuterSize_AndTheBoxIsCentered()
    {
        var (pack, factory) = Create(p => p.Manifest["effectMargin"] = 0.2);
        using var _ = pack;
        var (view, options) = NewView(factory);

        Assert.Equal(options.Outer.Width, view.Visual.Width);
        Assert.Equal(options.Outer.Height, view.Visual.Height);
        Assert.False(view.Visual.IsHitTestVisible);
        Assert.Equal(new SpriteOptions(80, new Size(80, 80), new Size(112, 112)).Outer, options.Outer);
    }

    [AvaloniaFact]
    public void EveryState_CanBeSet_AndTheSpriteStillPaints()
    {
        var (pack, factory) = Create(p =>
        {
            foreach (var state in new[] { "listening", "transcribing", "thinking", "tool", "streaming", "done", "attention", "error", "sleep" })
                p.AddAnimation(state);
        });
        using var _ = pack;
        var (view, options) = NewView(factory);

        foreach (var state in Enum.GetValues<SpriteState>())
        {
            view.SetState(state);
            for (var i = 0; i < 5; i++)
                view.Advance(Frame);
            var snapshot = Snapshot.Take(view.Visual, options.Outer);

            Assert.True(snapshot.AllPixels().Count(p => p >> 24 != 0) > 100, state.ToString());
        }
    }

    [AvaloniaFact]
    public void AnInteractionSequence_DoesNotThrow()
    {
        var (pack, factory) = Create(p =>
        {
            p.AddAnimation("poke.press", loop: false, hold: "last");
            p.AddAnimation("poke.release", loop: false, next: "idle");
            p.AddAnimation("drag");
        });
        using var _ = pack;
        var (view, options) = NewView(factory);
        SpriteInteraction[] sequence =
        [
            new SpriteInteraction.Near(true), new SpriteInteraction.Look(new Vector(0.5, 0.5)),
            new SpriteInteraction.PokeBegin(BoxCenter(options)), new SpriteInteraction.PokeEnd(), new SpriteInteraction.PokeEnd(),
            new SpriteInteraction.DragBegin(), new SpriteInteraction.DragEnd(), new SpriteInteraction.DragEnd(), new SpriteInteraction.Near(false),
        ];

        foreach (var interaction in sequence)
        {
            view.OnInteraction(interaction);
            for (var i = 0; i < 4; i++)
                view.Advance(Frame);
        }

        Assert.Contains(Snapshot.Take(view.Visual, options.Outer).AllPixels(), p => p >> 24 != 0);
    }

    [AvaloniaFact]
    public void ABubbleAnchor_IsTheManifestRatio_InsideTheBox()
    {
        var (pack, factory) = Create(p => p.Manifest["bubbleAnchor"] = new JsonObject { ["x"] = 0.25, ["y"] = 0.5 });
        using var _ = pack;
        var (view, options) = NewView(factory);

        var box = options.BoxRect;
        Assert.Equal(box.X + 0.25 * box.Width, view.BubbleAnchor.X, 6);
        Assert.Equal(box.Y + 0.5 * box.Height, view.BubbleAnchor.Y, 6);
    }

    // ---- 切格繪製 ----

    [AvaloniaFact]
    public void TheCurrentFrame_IsTheOneThatGetsDrawn()
    {
        var (pack, factory) = Create();
        using var _ = pack;
        var (view, options) = NewView(factory);

        Assert.Equal(TestPng.FrameColor(0), CenterColor(view, options));
        view.Advance(Frame);
        Assert.Equal(TestPng.FrameColor(1), CenterColor(view, options));
        view.Advance(Frame);
        Assert.Equal(TestPng.FrameColor(2), CenterColor(view, options));   // 第二列
        view.Advance(Frame);
        view.Advance(Frame);
        Assert.Equal(TestPng.FrameColor(0), CenterColor(view, options));   // 循環
    }

    [AvaloniaFact]
    public void AStateChange_SwitchesToThatAnimationsSheet()
    {
        var (pack, factory) = Create(p => p.AddAnimation("thinking", frames: 2, columns: 2));
        using var _ = pack;
        var (view, options) = NewView(factory);

        view.SetState(SpriteState.Thinking);

        Assert.Equal(TestPng.FrameColor(0), CenterColor(view, options));   // thinking 的第 0 格（同一個調色規則）
        Assert.Equal(1, factory.Definition.Animations["thinking"].Rows);
    }

    [AvaloniaFact]
    public void ThePokeAnimation_ShowsWhilePressed_AndTheStateReturnsAfterTheRelease()
    {
        var (pack, factory) = Create(p =>
        {
            p.AddAnimation("poke.press", frames: 2, columns: 2, loop: false, hold: "last");
            p.AddAnimation("poke.release", frames: 2, columns: 2, loop: false);
        });
        using var _ = pack;
        var (view, options) = NewView(factory);
        view.Advance(Frame);   // idle 第 1 格

        view.OnInteraction(new SpriteInteraction.PokeBegin(BoxCenter(options)));
        view.Advance(Frame);
        view.Advance(Frame);
        Assert.Equal(TestPng.FrameColor(1), CenterColor(view, options));   // press 停在最後一格（第 1 格）

        view.OnInteraction(new SpriteInteraction.PokeEnd());
        view.Advance(Frame);
        view.Advance(Frame);   // release 播完
        Assert.Equal(TestPng.FrameColor(1), CenterColor(view, options));   // 回到 idle：停在原來的第 1 格
    }

    [AvaloniaFact]
    public void TheSprite_SitsOnTheBottomOfItsBox()
    {
        var (pack, factory) = Create(p => p.WriteBytes("idle.png", TestPng.Create(40, 40, (_, y) => y % 20 == 19 ? 0xFFFF0000u : 0)));   // 每格最底下一列是紅的
        using var _ = pack;
        var (view, options) = NewView(factory);

        var snapshot = Snapshot.Take(view.Visual, options.Outer);
        var bottom = (int)options.BoxRect.Bottom - 1;

        Assert.Equal(0xFFFF0000u, snapshot.Pixel(snapshot.Width / 2, bottom));
        Assert.True(snapshot.IsTransparent(snapshot.Width / 2, bottom - 8));
    }

    [AvaloniaFact]
    public void ACellWithADifferentAspect_IsFittedAndSitsOnTheBottom()
    {
        var (pack, factory) = Create(p =>
        {
            p.Manifest["box"] = new JsonObject { ["width"] = 80, ["height"] = 80 };
            p.WriteBytes("idle.png", TestPng.Create(80, 20, (_, _) => 0xFF00FF00u));   // 格子 40×10（4:1），整格填滿
        });
        using var _ = pack;
        var (view, options) = NewView(factory);

        var snapshot = Snapshot.Take(view.Visual, options.Outer);

        Assert.Equal(0xFF00FF00u, snapshot.Pixel(snapshot.Width / 2, snapshot.Height - 2));   // 底部有畫
        Assert.True(snapshot.IsTransparent(snapshot.Width / 2, 5));                           // 上方是空的（等比例縮放，不拉伸）
    }

    // ---- 縮放：smooth／pixelated、整數倍貼齊 ----

    /// <summary>一格 20×20，左半紅、右半藍：縮放到非整數倍時，平滑會在交界產生紫色，最近鄰不會。</summary>
    private static (SpritePack Pack, AssetSpriteFactory Factory) TwoColor(SpriteRendering rendering, double box, int? pixelGrid = null) => Create(p =>
    {
        p.Manifest["box"] = new JsonObject { ["width"] = box, ["height"] = box };
        p.Manifest["rendering"] = rendering == SpriteRendering.Pixelated ? "pixelated" : "smooth";
        if (pixelGrid is { } grid)
            p.Manifest["pixelGrid"] = grid;
        p.WriteBytes("idle.png", TestPng.Create(40, 40, (x, _) => x % 20 < 10 ? 0xFFFF0000u : 0xFF0000FFu));
    }, box);

    [AvaloniaFact]
    public void Pixelated_NeverCreatesBlendedColors()
    {
        var (pack, factory) = TwoColor(SpriteRendering.Pixelated, box: 33);   // 33/20 = 1.65 倍
        using var _ = pack;
        var (view, options) = NewView(factory, tier: 80);

        var colors = Snapshot.Take(view.Visual, options.Outer).AllPixels().Where(p => p >> 24 != 0).ToHashSet();

        Assert.Equal(new HashSet<uint> { 0xFFFF0000u, 0xFF0000FFu }, colors);
    }

    [AvaloniaFact]
    public void Smooth_BlendsAtTheEdgesWhenScalingByAFraction()
    {
        var (pack, factory) = TwoColor(SpriteRendering.Smooth, box: 33);
        using var _ = pack;
        var (view, options) = NewView(factory, tier: 80);

        var colors = Snapshot.Take(view.Visual, options.Outer).AllPixels().Where(p => p >> 24 != 0).ToHashSet();

        Assert.True(colors.Count > 2, "平滑縮放應該在交界產生混色");
    }

    /// <summary>28 個邏輯像素、每個 1 px，紅藍交替：格子 28×28。</summary>
    private static (SpritePack Pack, AssetSpriteFactory Factory) Stripes(double box, int? pixelGrid) => Create(p =>
    {
        p.Manifest["box"] = new JsonObject { ["width"] = box, ["height"] = box };
        p.Manifest["rendering"] = "pixelated";
        if (pixelGrid is { } grid)
            p.Manifest["pixelGrid"] = grid;
        p.Animations["idle"] = new JsonObject { ["source"] = "idle.png", ["frames"] = 1, ["columns"] = 1, ["fps"] = 8, ["loop"] = true };
        p.WriteBytes("idle.png", TestPng.Create(28, 28, (x, _) => x % 2 == 0 ? 0xFFFF0000u : 0xFF0000FFu));
    }, box);

    [AvaloniaFact]
    public void WithAPixelGrid_EveryLogicalPixelIsTheSameWidth()
    {
        var (pack, factory) = Stripes(box: 80, pixelGrid: 28);   // 80 ÷ 28 = 2.86 → 每個邏輯像素 2 裝置像素，整張 56 px
        using var _ = pack;
        var (view, options) = NewView(factory);

        var snapshot = Snapshot.Take(view.Visual, options.Outer);
        var y = snapshot.Height - 5;
        var runs = snapshot.Runs(y, 0, snapshot.Width);

        // 兩側是透明的邊，中間 28 段條紋，每段剛好 2 px
        var stripes = runs.Skip(1).Take(28).ToList();
        Assert.Equal(28, stripes.Count);
        Assert.All(stripes, w => Assert.Equal(2, w));
    }

    [AvaloniaFact]
    public void WithoutAPixelGrid_TheStripesAreUneven_WhichIsWhyThePixelGridExists()
    {
        var (pack, factory) = Stripes(box: 80, pixelGrid: null);   // 80 ÷ 28 = 2.857 倍的最近鄰縮放
        using var _ = pack;
        var (view, options) = NewView(factory);

        var snapshot = Snapshot.Take(view.Visual, options.Outer);
        var runs = snapshot.Runs(snapshot.Height - 5, 0, snapshot.Width).Where(w => w > 1).ToList();

        Assert.True(runs.Distinct().Count() > 1, "非整數倍時條紋寬度不一致（2 與 3 px 交錯）");
    }

    [AvaloniaFact]
    public void APixelGridSprite_OnARetinaDisplay_UsesWholeDevicePixelsToo()
    {
        var (pack, factory) = Stripes(box: 80, pixelGrid: 28);
        using var _ = pack;
        var (view, options) = NewView(factory);

        ((SpriteSheetControl)view.Visual).RenderScalingOverride = 2;   // 沒有視窗可掛，直接指定 2 倍
        var snapshot = Snapshot.Take(view.Visual, options.Outer, scaling: 2);   // 160×160 裝置像素：160 ÷ 28 = 5.7 → 5 px 一個
        var stripes = snapshot.Runs(snapshot.Height - 5, 0, snapshot.Width).Skip(1).Take(28).ToList();

        Assert.Equal(28, stripes.Count);
        Assert.All(stripes, w => Assert.Equal(5, w));
    }

    [AvaloniaFact]
    public void APixelGridSprite_StaysOnTheBottom_AndCenteredHorizontally()
    {
        var (pack, factory) = Stripes(box: 80, pixelGrid: 28);
        using var _ = pack;
        var (view, options) = NewView(factory);

        var snapshot = Snapshot.Take(view.Visual, options.Outer);

        Assert.False(snapshot.IsTransparent(snapshot.Width / 2, snapshot.Height - 1));   // 底邊那一列有畫
        Assert.True(snapshot.IsTransparent(snapshot.Width / 2, snapshot.Height - 57));   // 56 px 高，再上去就沒有了
        Assert.True(snapshot.IsTransparent(5, snapshot.Height - 5));                     // 左側留白
        Assert.True(snapshot.IsTransparent(snapshot.Width - 6, snapshot.Height - 5));    // 右側留白
    }

    // ---- 命中範圍 ----

    private static (SpritePack Pack, AssetSpriteFactory Factory) Shaped(string shape, double margin = 0) => Create(p =>
    {
        p.Manifest["hitShape"] = shape;
        p.Manifest["effectMargin"] = margin;
    });

    [AvaloniaFact]
    public void Alpha_HitsWhereTheCurrentFrameIsOpaque_AndNotInTheTransparentCorners()
    {
        var (pack, factory) = Shaped("alpha");
        using var _ = pack;
        var (view, options) = NewView(factory);
        var box = options.BoxRect;

        Assert.True(view.HitTest(box.Center));                                  // 中間的方塊
        Assert.False(view.HitTest(new Point(box.X + 1, box.Y + 1)));            // 左上角的透明處
        Assert.False(view.HitTest(new Point(box.Right - 1, box.Bottom - 1)));   // 右下角
    }

    [AvaloniaFact]
    public void Alpha_IncludesOneLogicalPixelAroundTheOpaqueArea_ButNoMore()
    {
        var (pack, factory) = Shaped("alpha");
        using var _ = pack;
        var (view, options) = NewView(factory);
        var box = options.BoxRect;
        // 方塊佔格子的 1/4～3/4（box 80 DIP 的 20～60）；容許 1 個邏輯像素＝格子寬 ÷ 28 ≈ 0.7 px → 取 1 個來源像素＝4 DIP
        var justOutside = new Point(box.X + 20 - 3, box.Center.Y);   // 方塊左緣外 3 DIP
        var farOutside = new Point(box.X + 20 - 12, box.Center.Y);

        Assert.True(view.HitTest(justOutside));
        Assert.False(view.HitTest(farOutside));
    }

    [AvaloniaFact]
    public void Alpha_FollowsTheCurrentFrame()
    {
        var (pack, factory) = Create(p =>
        {
            p.Manifest["hitShape"] = "alpha";
            // 第 1 格只有左半有東西，第 2 格只有右半有東西
            p.WriteBytes("idle.png", TestPng.Create(40, 40, (x, y) => y < 20 && ((x < 20 && x < 10) || (x >= 20 && x >= 30)) ? 0xFFAA0000u : 0));
        });
        using var _ = pack;
        var (view, options) = NewView(factory);
        var box = options.BoxRect;
        var left = new Point(box.X + box.Width * 0.2, box.Y + box.Height * 0.5);
        var right = new Point(box.X + box.Width * 0.8, box.Y + box.Height * 0.5);

        Assert.True(view.HitTest(left));
        Assert.False(view.HitTest(right));

        view.Advance(Frame);   // 第 2 格

        Assert.False(view.HitTest(left));
        Assert.True(view.HitTest(right));
    }

    [AvaloniaFact]
    public void Alpha_FeetAtTheBottomCorners_AreHit_WhichAnEllipseWouldMiss()
    {
        var (pack, factory) = Create(p =>
        {
            p.Manifest["hitShape"] = "alpha";
            p.WriteBytes("idle.png", TestPng.Create(40, 40, (x, y) => y % 20 >= 16 && x % 20 is < 4 or >= 16 ? 0xFF223344u : 0));   // 兩隻腳在每格的左右下角
        });
        using var _ = pack;
        var (view, options) = NewView(factory);
        var box = options.BoxRect;
        var leftFoot = new Point(box.X + 3, box.Bottom - 3);

        Assert.True(view.HitTest(leftFoot));

        var (pack2, ellipse) = Shaped("ellipse");
        using var _2 = pack2;
        var (ellipseView, ellipseOptions) = NewView(ellipse);
        Assert.False(ellipseView.HitTest(new Point(ellipseOptions.BoxRect.X + 3, ellipseOptions.BoxRect.Bottom - 3)));
    }

    [AvaloniaTheory]
    [InlineData("alpha")]
    [InlineData("rect")]
    [InlineData("ellipse")]
    public void EveryHitShape_NeverReachesOutsideTheBox(string shape)
    {
        var (pack, factory) = Shaped(shape, margin: 0.3);
        using var _ = pack;
        var (view, options) = NewView(factory);
        var box = options.BoxRect;

        for (var x = 0.0; x < options.Outer.Width; x += 2)
        {
            for (var y = 0.0; y < options.Outer.Height; y += 2)
            {
                var p = new Point(x, y);
                if (view.HitTest(p))
                    Assert.True(box.Contains(p), $"{shape}: {p} 在角色框外卻命中");
            }
        }
    }

    [AvaloniaFact]
    public void Rect_HitsTheWholeBox_Ellipse_OnlyTheInscribedOval()
    {
        var (rectPack, rect) = Shaped("rect");
        using var _1 = rectPack;
        var (ellipsePack, ellipse) = Shaped("ellipse");
        using var _2 = ellipsePack;
        var (rectView, rectOptions) = NewView(rect);
        var (ellipseView, ellipseOptions) = NewView(ellipse);

        var corner = new Point(rectOptions.BoxRect.X + 2, rectOptions.BoxRect.Y + 2);
        Assert.True(rectView.HitTest(corner));
        Assert.False(ellipseView.HitTest(new Point(ellipseOptions.BoxRect.X + 2, ellipseOptions.BoxRect.Y + 2)));
        Assert.True(ellipseView.HitTest(ellipseOptions.BoxRect.Center));
        Assert.False(rectView.HitTest(new Point(rectOptions.BoxRect.Right + 1, rectOptions.BoxRect.Center.Y)));
    }

    [AvaloniaFact]
    public void HitTest_DoesNotDependOnLayout_OrOnHavingBeenDrawn()
    {
        var (pack, factory) = Shaped("alpha");
        using var _ = pack;
        var (view, options) = NewView(factory);   // 沒有 Measure／Arrange、也沒有畫過

        Assert.True(view.HitTest(options.BoxRect.Center));
    }

    // ---- 幀率 ----

    [AvaloniaFact]
    public void PreferredFps_IsTheAnimationsFps_AndDropsToTheMinimumWhenTheFrameIsHeld()
    {
        var (pack, factory) = Create(p =>
        {
            p.AddAnimation("sleep", fps: 4);
            p.AddAnimation("error", frames: 3, columns: 3, fps: 10, loop: false, hold: "last");
            p.AddAnimation("poke.press", frames: 2, columns: 2, fps: 16, loop: false, hold: "last");
        });
        using var _ = pack;
        var (view, _) = NewView(factory);

        Assert.Equal(10, view.PreferredFps);   // idle

        view.SetState(SpriteState.Sleep);
        Assert.Equal(4, view.PreferredFps);    // 休眠：4 fps

        view.SetState(SpriteState.Error);
        Assert.Equal(10, view.PreferredFps);
        for (var i = 0; i < 6; i++)
            view.Advance(Frame);
        Assert.Equal(1, view.PreferredFps);    // 停在最後一格：不用再畫

        view.OnInteraction(new SpriteInteraction.PokeBegin(new Point(40, 40)));
        Assert.Equal(16, view.PreferredFps);   // 互動動畫的 fps
    }

    [AvaloniaFact]
    public void AFractionalFps_IsRoundedUp()
    {
        var (pack, factory) = Create(p => p.AddAnimation("sleep", fps: 4.5));
        using var _ = pack;
        var (view, _) = NewView(factory);

        view.SetState(SpriteState.Sleep);

        Assert.Equal(5, view.PreferredFps);
    }

    // ---- 多個 view 共用 ----

    [AvaloniaFact]
    public void SeveralViews_OfTheSameSprite_ShareTheDecodedSheets_ButPlayIndependently()
    {
        var (pack, factory) = Create(p => p.AddAnimation("thinking"));
        using var _ = pack;
        var (first, firstOptions) = NewView(factory);
        var (second, secondOptions) = NewView(factory);

        first.Advance(Frame);
        second.SetState(SpriteState.Thinking);

        Assert.Equal(TestPng.FrameColor(1), CenterColor(first, firstOptions));
        Assert.Equal(TestPng.FrameColor(0), CenterColor(second, secondOptions));
    }

    // ---- 各種尺寸檔位 ----

    [AvaloniaTheory]
    [InlineData(64)]
    [InlineData(80)]
    [InlineData(104)]
    public void EveryTier_Paints(double tier)
    {
        var (pack, factory) = Create();
        using var _ = pack;
        var (view, options) = NewView(factory, tier);

        Assert.Contains(Snapshot.Take(view.Visual, options.Outer).AllPixels(), p => p >> 24 != 0);
        Assert.True(view.HitTest(options.BoxRect.Center));
    }
}
