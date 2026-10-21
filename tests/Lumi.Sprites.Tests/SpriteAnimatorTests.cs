using Avalonia;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;

namespace Lumi.Sprites.Tests;

/// <summary>plan/08「播放」規則表的每一列。用 10 fps：每一格 0.1 秒，算起來好看。</summary>
public class SpriteAnimatorTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(0.1);

    private static AssetAnimation Anim(string key, int frames = 4, double fps = 10, bool loop = true, string? next = null, bool hold = false) =>
        new(key, key + ".png", "/x/" + key + ".png", frames, frames, fps, loop, next, hold, new Size(frames * 10, 10));

    private static SpriteAnimator Create(params AssetAnimation[] extra)
    {
        var all = new Dictionary<string, AssetAnimation> { ["idle"] = Anim("idle") };
        foreach (var animation in extra)
            all[animation.Key] = animation;
        return new SpriteAnimator(all);
    }

    private static void Play(SpriteAnimator animator, int frames)
    {
        for (var i = 0; i < frames; i++)
            animator.Advance(Frame + TimeSpan.FromMilliseconds(1));   // 多 1 毫秒：避免浮點誤差剛好差一格
    }

    // ---- 基本 ----

    [Fact]
    public void ItStartsOnTheFirstFrameOfIdle()
    {
        var animator = Create();

        Assert.Equal(new AnimationFrame("idle", 0), animator.Current);
        Assert.Equal(10, animator.Fps);
        Assert.False(animator.IsStatic);
        Assert.False(animator.HasOverlay);
    }

    [Fact]
    public void ItRequiresIdle()
    {
        Assert.Throws<ArgumentException>(() => new SpriteAnimator(new Dictionary<string, AssetAnimation> { ["thinking"] = Anim("thinking") }));
    }

    [Fact]
    public void TimeAdvancesTheFrame_AtTheAnimationsFps()
    {
        var animator = Create();

        animator.Advance(TimeSpan.FromSeconds(0.05));
        Assert.Equal(0, animator.Current.Index);   // 還不到一格

        animator.Advance(TimeSpan.FromSeconds(0.06));
        Assert.Equal(1, animator.Current.Index);   // 累計 0.11 秒

        Play(animator, 2);
        Assert.Equal(3, animator.Current.Index);
    }

    [Fact]
    public void ALoopingAnimation_WrapsAround()
    {
        var animator = Create();

        Play(animator, 4);

        Assert.Equal(new AnimationFrame("idle", 0), animator.Current);
        Play(animator, 5);
        Assert.Equal(1, animator.Current.Index);
        Assert.False(animator.IsStatic);
    }

    [Fact]
    public void ADifferentFps_ChangesTheStepSize()
    {
        var animator = Create(Anim("thinking", frames: 8, fps: 4));
        animator.SetState(SpriteState.Thinking);

        animator.Advance(TimeSpan.FromSeconds(0.2));   // 4 fps：0.25 秒一格
        Assert.Equal(0, animator.Current.Index);
        animator.Advance(TimeSpan.FromSeconds(0.06));
        Assert.Equal(1, animator.Current.Index);
        Assert.Equal(4, animator.Fps);
    }

    [Fact]
    public void ZeroOrNegativeSteps_ChangeNothing()
    {
        var animator = Create();
        Play(animator, 2);

        animator.Advance(TimeSpan.Zero);
        animator.Advance(TimeSpan.FromSeconds(-5));

        Assert.Equal(2, animator.Current.Index);
    }

    [Fact]
    public void AHugeStep_IsCappedAtAQuarterSecond()
    {
        var animator = Create(Anim("thinking", frames: 100, fps: 10, loop: true));
        animator.SetState(SpriteState.Thinking);

        animator.Advance(TimeSpan.FromMinutes(10));   // 電腦睡眠醒來後的第一幀

        Assert.Equal(2, animator.Current.Index);   // 0.25 秒 ÷ 0.1 = 2 格
    }

    [Fact]
    public void AStepLongerThanTheWholeLoop_StillLandsInsideTheAnimation()
    {
        var animator = Create(Anim("thinking", frames: 2, fps: 60));
        animator.SetState(SpriteState.Thinking);

        animator.Advance(TimeSpan.FromSeconds(0.25));   // 15 格，只有 2 格

        Assert.InRange(animator.Current.Index, 0, 1);
    }

    // ---- 狀態 ----

    [Fact]
    public void SettingAState_StartsItsAnimationFromTheFirstFrame()
    {
        var animator = Create(Anim("thinking"));
        Play(animator, 2);

        animator.SetState(SpriteState.Thinking);

        Assert.Equal(new AnimationFrame("thinking", 0), animator.Current);
        Assert.Equal(SpriteState.Thinking, animator.State);
    }

    [Fact]
    public void SettingTheSameStateAgain_DoesNotRestart()
    {
        var animator = Create(Anim("thinking"));
        animator.SetState(SpriteState.Thinking);
        Play(animator, 2);

        animator.SetState(SpriteState.Thinking);

        Assert.Equal(2, animator.Current.Index);
    }

    [Fact]
    public void AStateWithoutAnAnimation_UsesIdle()
    {
        var animator = Create();   // 只有 idle
        Play(animator, 1);

        animator.SetState(SpriteState.Streaming);   // 殼會先解析退化，這是保險

        Assert.Equal("idle", animator.Current.Key);
        Assert.Equal(0, animator.Current.Index);
    }

    [Fact]
    public void SettingIdleAgainAfterAnotherState_ReturnsToIdleFromTheStart()
    {
        var animator = Create(Anim("thinking"));
        animator.SetState(SpriteState.Thinking);

        animator.SetState(SpriteState.Idle);

        Assert.Equal(new AnimationFrame("idle", 0), animator.Current);
    }

    // ---- 播完之後 ----

    [Fact]
    public void ANonLoopingAnimation_ContinuesWithNext_FromItsFirstFrame()
    {
        var animator = Create(Anim("done", frames: 3, loop: false, next: "thinking"), Anim("thinking", frames: 5));
        animator.SetState(SpriteState.Done);

        Play(animator, 2);
        Assert.Equal(new AnimationFrame("done", 2), animator.Current);
        Play(animator, 1);   // 第 3 格播完

        Assert.Equal(new AnimationFrame("thinking", 0), animator.Current);
        Play(animator, 6);
        Assert.Equal("thinking", animator.Current.Key);   // next 是循環的動畫：不會再回 done
        Assert.False(animator.IsStatic);
    }

    [Fact]
    public void ANextAnimationsLeftoverTime_IsNotCarriedOver()
    {
        var animator = Create(Anim("done", frames: 2, loop: false, next: "thinking"), Anim("thinking", frames: 5));
        animator.SetState(SpriteState.Done);
        Play(animator, 1);

        animator.Advance(TimeSpan.FromSeconds(0.25));   // 播完 done 後還有剩，但 thinking 從第 0 格開始

        Assert.Equal(new AnimationFrame("thinking", 0), animator.Current);
    }

    [Fact]
    public void WithoutNextOrHold_ANonLoopingStateAnimation_ReturnsToIdle()
    {
        var animator = Create(Anim("done", frames: 2, loop: false));
        animator.SetState(SpriteState.Done);

        Play(animator, 3);

        Assert.Equal("idle", animator.Current.Key);
        Assert.False(animator.IsStatic);
    }

    [Fact]
    public void HoldLast_StaysOnTheLastFrame_AndIsStatic()
    {
        var animator = Create(Anim("error", frames: 3, loop: false, hold: true));
        animator.SetState(SpriteState.Error);

        Play(animator, 10);

        Assert.Equal(new AnimationFrame("error", 2), animator.Current);
        Assert.True(animator.IsStatic);
        Play(animator, 10);
        Assert.Equal(2, animator.Current.Index);   // 一直停著
    }

    [Fact]
    public void HoldLast_WinsOverReturningToIdle()
    {
        var animator = Create(Anim("error", frames: 2, loop: false, hold: true));
        animator.SetState(SpriteState.Error);

        Play(animator, 5);

        Assert.Equal("error", animator.Current.Key);
    }

    [Fact]
    public void ANonLoopingIdle_StopsOnItsLastFrame()
    {
        var animator = new SpriteAnimator(new Dictionary<string, AssetAnimation> { ["idle"] = Anim("idle", frames: 3, loop: false) });

        Play(animator, 10);

        Assert.Equal(new AnimationFrame("idle", 2), animator.Current);
        Assert.True(animator.IsStatic);
    }

    [Fact]
    public void ASingleFrameAnimation_IsStatic()
    {
        var animator = new SpriteAnimator(new Dictionary<string, AssetAnimation> { ["idle"] = Anim("idle", frames: 1) });

        Assert.True(animator.IsStatic);
        Play(animator, 5);
        Assert.Equal(0, animator.Current.Index);
    }

    [Fact]
    public void AStaticFrame_StartsMovingAgainWhenTheStateChanges()
    {
        var animator = Create(Anim("error", frames: 2, loop: false, hold: true), Anim("thinking"));
        animator.SetState(SpriteState.Error);
        Play(animator, 5);
        Assert.True(animator.IsStatic);

        animator.SetState(SpriteState.Thinking);

        Assert.False(animator.IsStatic);
        Assert.Equal(new AnimationFrame("thinking", 0), animator.Current);
    }

    [Fact]
    public void AnimationsThatPointAtEachOther_NeverHang()
    {
        var animator = Create(Anim("done", frames: 1, loop: false, next: "error"), Anim("error", frames: 1, loop: false, next: "done"));
        animator.SetState(SpriteState.Done);

        for (var i = 0; i < 100; i++)
            animator.Advance(Frame);

        Assert.Contains(animator.Current.Key, new[] { "done", "error" });
    }

    // ---- 戳 ----

    private static SpriteAnimator WithPoke(bool release = true) => Create(
        Anim("thinking"),
        Anim("poke.press", frames: 3, loop: false, hold: true),
        release ? Anim("poke.release", frames: 3, loop: false, next: "idle") : Anim("drag"));

    [Fact]
    public void PokeBegin_PlaysPress_AndHoldsItsLastFrameUntilTheRelease()
    {
        var animator = WithPoke();

        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));
        Assert.Equal(new AnimationFrame("poke.press", 0), animator.Current);
        Assert.True(animator.HasOverlay);

        Play(animator, 10);
        Assert.Equal(new AnimationFrame("poke.press", 2), animator.Current);   // 按住：停在最後一格
        Assert.True(animator.IsStatic);
    }

    [Fact]
    public void PokeEnd_PlaysRelease_ThenReturnsToTheCurrentState()
    {
        var animator = WithPoke();
        animator.SetState(SpriteState.Thinking);
        Play(animator, 2);   // 底層停在 thinking 的第 2 格
        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));
        Play(animator, 5);

        animator.OnInteraction(new SpriteInteraction.PokeEnd());
        Assert.Equal(new AnimationFrame("poke.release", 0), animator.Current);
        Play(animator, 2);
        Assert.Equal("poke.release", animator.Current.Key);
        Play(animator, 1);   // 播完

        Assert.False(animator.HasOverlay);
        Assert.Equal(new AnimationFrame("thinking", 2), animator.Current);   // 回到底層目前的狀態（不是 release 的 next: idle），並從原來的位置繼續
    }

    [Fact]
    public void TheBaseAnimationIsPaused_WhileAnOverlayIsPlaying()
    {
        var animator = WithPoke();
        animator.SetState(SpriteState.Thinking);
        Play(animator, 1);
        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));

        Play(animator, 20);
        animator.OnInteraction(new SpriteInteraction.PokeEnd());
        Play(animator, 3);   // release 剛好播完（3 格），底層一格都沒有動過

        Assert.False(animator.HasOverlay);
        Assert.Equal(new AnimationFrame("thinking", 1), animator.Current);
    }

    [Fact]
    public void WithoutAReleaseAnimation_PokeEndJustClearsTheOverlay()
    {
        var animator = WithPoke(release: false);
        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));

        animator.OnInteraction(new SpriteInteraction.PokeEnd());

        Assert.False(animator.HasOverlay);
        Assert.Equal("idle", animator.Current.Key);
    }

    [Fact]
    public void WithoutAPressAnimation_PokesAreIgnored()
    {
        var animator = Create(Anim("poke.release", frames: 2, loop: false));

        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));
        Assert.False(animator.HasOverlay);

        animator.OnInteraction(new SpriteInteraction.PokeEnd());   // 前面沒有按下：忽略，不會單獨播放 release
        Assert.False(animator.HasOverlay);
    }

    [Fact]
    public void PokeEndWithoutAPokeBegin_IsIgnored()
    {
        var animator = WithPoke();

        animator.OnInteraction(new SpriteInteraction.PokeEnd());

        Assert.False(animator.HasOverlay);
        Assert.Equal("idle", animator.Current.Key);
    }

    [Fact]
    public void APokeEndTwice_DoesNotRestartTheRelease()
    {
        var animator = WithPoke();
        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));
        animator.OnInteraction(new SpriteInteraction.PokeEnd());
        Play(animator, 2);

        animator.OnInteraction(new SpriteInteraction.PokeEnd());

        Assert.Equal(new AnimationFrame("poke.release", 2), animator.Current);
    }

    // ---- 拖曳 ----

    [Fact]
    public void Drag_LoopsUntilDragEnd_ThenReturnsToTheBase()
    {
        var animator = Create(Anim("drag", frames: 3, fps: 10));
        animator.OnInteraction(new SpriteInteraction.DragBegin());

        Play(animator, 7);
        Assert.Equal("drag", animator.Current.Key);
        Assert.False(animator.IsStatic);

        animator.OnInteraction(new SpriteInteraction.DragEnd());
        Assert.False(animator.HasOverlay);
        Assert.Equal("idle", animator.Current.Key);
    }

    [Fact]
    public void WithoutADragAnimation_DraggingChangesNothing()
    {
        var animator = Create();
        Play(animator, 1);

        animator.OnInteraction(new SpriteInteraction.DragBegin());
        animator.OnInteraction(new SpriteInteraction.DragEnd());

        Assert.False(animator.HasOverlay);
        Assert.Equal(new AnimationFrame("idle", 1), animator.Current);
    }

    [Fact]
    public void DragBegin_ReplacesAPoke_ButAPokeDuringADragIsIgnored()
    {
        var animator = Create(Anim("drag"), Anim("poke.press", frames: 2, loop: false, hold: true), Anim("poke.release", frames: 2, loop: false));
        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));

        animator.OnInteraction(new SpriteInteraction.DragBegin());
        Assert.Equal("drag", animator.Current.Key);

        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));
        Assert.Equal("drag", animator.Current.Key);
        animator.OnInteraction(new SpriteInteraction.PokeEnd());
        Assert.Equal("drag", animator.Current.Key);
    }

    [Fact]
    public void DragEndWithoutADrag_DoesNotCancelAPoke()
    {
        var animator = WithPoke();
        animator.OnInteraction(new SpriteInteraction.PokeBegin(new Point(5, 5)));

        animator.OnInteraction(new SpriteInteraction.DragEnd());

        Assert.True(animator.HasOverlay);
        Assert.Equal("poke.press", animator.Current.Key);
    }

    [Fact]
    public void AStateChangeDuringAnOverlay_AppliesWhenTheOverlayEnds()
    {
        var animator = Create(Anim("drag"), Anim("thinking"));
        animator.OnInteraction(new SpriteInteraction.DragBegin());

        animator.SetState(SpriteState.Thinking);
        Assert.Equal("drag", animator.Current.Key);   // 覆蓋層優先
        animator.OnInteraction(new SpriteInteraction.DragEnd());

        Assert.Equal(new AnimationFrame("thinking", 0), animator.Current);
    }

    [Fact]
    public void TheFpsFollowsTheVisibleAnimation()
    {
        var animator = Create(Anim("sleep", fps: 4), Anim("drag", fps: 12));
        animator.SetState(SpriteState.Sleep);
        Assert.Equal(4, animator.Fps);

        animator.OnInteraction(new SpriteInteraction.DragBegin());
        Assert.Equal(12, animator.Fps);

        animator.OnInteraction(new SpriteInteraction.DragEnd());
        Assert.Equal(4, animator.Fps);
    }

    // ---- 不相關的互動 ----

    [Fact]
    public void NearAndLook_AreIgnored()
    {
        var animator = Create();
        Play(animator, 2);

        animator.OnInteraction(new SpriteInteraction.Near(true));
        animator.OnInteraction(new SpriteInteraction.Look(new Vector(1, -1)));

        Assert.False(animator.HasOverlay);
        Assert.Equal(new AnimationFrame("idle", 2), animator.Current);
    }
}
