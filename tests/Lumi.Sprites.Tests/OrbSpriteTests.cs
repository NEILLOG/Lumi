using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;

namespace Lumi.Sprites.Tests;

/// <summary>光球（headless，Skia 真的繪製）：每個狀態都能設定與畫出來、點擊範圍、幀率、互動序列。</summary>
public class OrbSpriteTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private static (ISpriteView View, SpriteOptions Options) Create(double tier = 80)
    {
        var factory = new OrbSpriteFactory();
        var options = SpriteLimits.Fit(factory.Manifest, tier).Options;
        return (factory.Create(options), options);
    }

    public static TheoryData<SpriteState> AllStates() => [.. Enum.GetValues<SpriteState>()];

    private static void Layout(ISpriteView view, SpriteOptions options)
    {
        view.Visual.Measure(options.Outer);
        view.Visual.Arrange(new Rect(options.Outer));
    }

    /// <summary>把角色畫成點陣圖，回傳有畫到的像素（alpha &gt; 0）數量與最大 alpha。</summary>
    private static (int Painted, int MaxAlpha) Paint(ISpriteView view, SpriteOptions options)
    {
        Layout(view, options);
        var size = new PixelSize((int)Math.Ceiling(options.Outer.Width), (int)Math.Ceiling(options.Outer.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(view.Visual);

        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        int painted = 0, max = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 0)
                painted++;
            max = Math.Max(max, pixels[i]);
        }
        return (painted, max);
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllStates))]
    public void EveryState_CanBeSet_Advanced_AndPaintsSomething(SpriteState state)
    {
        var (view, options) = Create();
        view.SetState(state);
        for (var i = 0; i < 60; i++)
            view.Advance(Frame);

        var (painted, maxAlpha) = Paint(view, options);

        Assert.True(painted > 200, $"{state}: 只畫到 {painted} 個像素");
        Assert.True(maxAlpha > 200, $"{state}: 最不透明的像素 alpha 只有 {maxAlpha}");
    }

    [AvaloniaTheory]
    [InlineData(64)]
    [InlineData(80)]
    [InlineData(104)]
    public void EveryTier_SizesTheVisualToTheOuterSize(double tier)
    {
        var (view, options) = Create(tier);

        Assert.Equal(options.Outer.Width, view.Visual.Width);
        Assert.Equal(options.Outer.Height, view.Visual.Height);
        Assert.True(Paint(view, options).Painted > 100);
    }

    [AvaloniaFact]
    public void OnlyTheBody_PaintsInsideTheBoxAtFullStrength_GlowReachesOutside()
    {
        var (view, options) = Create();
        view.SetState(SpriteState.Idle);
        for (var i = 0; i < 90; i++)
            view.Advance(Frame);

        var (painted, _) = Paint(view, options);
        var boxArea = options.Box.Width * options.Box.Height;

        Assert.True(painted > Math.PI / 4 * 80 * 80 * 0.8);          // 至少是光球本體那麼多
        Assert.True(painted > boxArea * 0.9);                         // 加上光暈，超過角色框的面積
        Assert.True(painted < options.Outer.Width * options.Outer.Height);
    }

    [AvaloniaFact]
    public void HitTest_IsTrueOnTheOrb_AndFalseOutsideTheBox()
    {
        var (view, options) = Create();
        var center = new Point(options.Outer.Width / 2, options.Outer.Height / 2);

        Assert.True(view.HitTest(center));
        Assert.True(view.HitTest(center + new Vector(30, 0)));            // 半徑 40 之內
        Assert.False(view.HitTest(center + new Vector(options.Box.Width, 0)));   // 框外
        Assert.False(view.HitTest(new Point(0, 0)));                       // Visual 的角落（光暈區，不是本體）
        Assert.False(view.HitTest(new Point(-5, -5)));
        Assert.False(view.HitTest(new Point(options.Outer.Width + 5, center.Y)));
    }

    [AvaloniaFact]
    public void HitTest_NeverReachesOutsideTheBox()
    {
        var (view, options) = Create();
        var box = options.BoxRect;

        for (var x = 0.0; x < options.Outer.Width; x += 2)
        {
            for (var y = 0.0; y < options.Outer.Height; y += 2)
            {
                var p = new Point(x, y);
                if (view.HitTest(p))
                    Assert.True(box.Contains(p), $"{p} 在角色框外卻被判定為命中");
            }
        }
    }

    [AvaloniaFact]
    public void HitTest_DoesNotDependOnLayout()
    {
        var (view, options) = Create();   // 還沒 Measure／Arrange

        Assert.True(view.HitTest(new Point(options.Outer.Width / 2, options.Outer.Height / 2)));
    }

    [AvaloniaFact]
    public void BubbleAnchor_IsAboveTheOrb_AndHorizontallyCentered()
    {
        var (view, options) = Create();
        var center = new Point(options.Outer.Width / 2, options.Outer.Height / 2);

        Assert.Equal(center.X, view.BubbleAnchor.X, 6);
        Assert.True(view.BubbleAnchor.Y < center.Y - 30);          // 在頭頂
        Assert.True(view.BubbleAnchor.Y >= options.BoxRect.Y);     // 不會高過角色框
    }

    [AvaloniaFact]
    public void PreferredFps_FollowsStateAndInteraction()
    {
        var (view, _) = Create();
        view.SetState(SpriteState.Idle);
        Assert.Equal(30, view.PreferredFps);

        view.OnInteraction(new SpriteInteraction.Near(true));
        Assert.Equal(60, view.PreferredFps);       // 靠近時眼睛跟著游標
        view.OnInteraction(new SpriteInteraction.Near(false));
        Assert.Equal(30, view.PreferredFps);

        view.SetState(SpriteState.Sleep);
        Assert.Equal(12, view.PreferredFps);

        view.SetState(SpriteState.Streaming);
        Assert.Equal(60, view.PreferredFps);

        view.SetState(SpriteState.Thinking);
        Assert.Equal(30, view.PreferredFps);
    }

    [AvaloniaFact]
    public void PokeAndDrag_RaiseTheFrameRate_UntilTheyAreOver()
    {
        var (view, options) = Create();
        view.SetState(SpriteState.Sleep);

        view.OnInteraction(new SpriteInteraction.PokeBegin(new Point(options.Outer.Width / 2, options.Outer.Height / 2)));
        Assert.Equal(60, view.PreferredFps);
        view.OnInteraction(new SpriteInteraction.PokeEnd());
        Assert.Equal(60, view.PreferredFps);       // 回彈中
        for (var i = 0; i < 120; i++)              // 1.9 秒後回彈結束
            view.Advance(Frame);
        Assert.Equal(12, view.PreferredFps);

        view.OnInteraction(new SpriteInteraction.DragBegin());
        Assert.Equal(60, view.PreferredFps);
        view.OnInteraction(new SpriteInteraction.DragEnd());
        Assert.Equal(12, view.PreferredFps);
    }

    [AvaloniaFact]
    public void AnInteractionSequence_DoesNotThrow_AndStillPaints()
    {
        var (view, options) = Create();
        var center = new Point(options.Outer.Width / 2, options.Outer.Height / 2);
        SpriteInteraction[] sequence =
        [
            new SpriteInteraction.Near(true),
            new SpriteInteraction.Look(new Vector(0.8, -0.4)),
            new SpriteInteraction.PokeBegin(center + new Vector(10, -5)),
            new SpriteInteraction.PokeEnd(),
            new SpriteInteraction.PokeEnd(),                    // 重複放開要安全
            new SpriteInteraction.DragBegin(),
            new SpriteInteraction.Look(new Vector(-1, 1)),
            new SpriteInteraction.DragEnd(),
            new SpriteInteraction.Near(false),
        ];

        foreach (var interaction in sequence)
        {
            view.OnInteraction(interaction);
            for (var i = 0; i < 10; i++)
                view.Advance(Frame);
        }

        Assert.True(Paint(view, options).Painted > 200);
    }

    [AvaloniaFact]
    public void Advance_WithHugeOrNegativeSteps_IsClamped()
    {
        var (view, options) = Create();

        view.Advance(TimeSpan.FromSeconds(60));   // 例如電腦睡眠醒來後的第一幀
        view.Advance(TimeSpan.FromSeconds(-1));
        view.Advance(TimeSpan.Zero);

        Assert.True(Paint(view, options).Painted > 200);
    }

    [AvaloniaFact]
    public void TheManifest_IsValid_AndSupportsEveryState()
    {
        var manifest = new OrbSpriteFactory().Manifest;

        Assert.Equal("lumi.orb", manifest.Id);
        Assert.Equal(SpriteManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(Enum.GetValues<SpriteState>().Length, manifest.Supported.Count);
        Assert.All(Enum.GetValues<SpriteState>(), s => Assert.Equal(s, SpriteStateResolver.Resolve(s, manifest)));
        Assert.Null(manifest.Voice);   // 光球沒有語氣
    }
}
