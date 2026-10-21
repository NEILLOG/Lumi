using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Assets;

/// <summary>現在要畫哪個動畫的第幾格（<see cref="Index"/> 從 0 起算）。</summary>
public readonly record struct AnimationFrame(string Key, int Index);

/// <summary>
/// 素材型角色的播放邏輯（純邏輯，不碰 Avalonia）：兩層——<b>底層</b>是目前狀態的動畫，<b>覆蓋層</b>是互動動畫（戳、拖曳），
/// 有覆蓋時它優先。時間由殼統一驅動（<see cref="Advance"/>），角色不自己開計時器。
/// <para>
/// 播完最後一格：<c>loop</c> 從頭；不循環就接 <c>next</c>；都沒有時，底層回到 <c>idle</c>（<c>idle</c> 本身停在最後一格）。
/// 覆蓋層不用 <c>next</c>：<c>poke.release</c> 播完就清掉覆蓋層、回到底層目前的狀態；<c>poke.press</c> 播完停在最後一格等放開；
/// <c>drag</c> 循環到 <c>DragEnd</c>。<c>Near</c>、<c>Look</c> 沒有對應的動畫，直接忽略。
/// </para>
/// </summary>
public sealed class SpriteAnimator
{
    /// <summary>單次 <see cref="Advance"/> 最多推進這麼久（電腦睡眠醒來後的第一幀不會一次跳很多格）。</summary>
    public static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(0.25);

    private const string Idle = "idle";
    private const string PokePress = "poke.press";
    private const string PokeRelease = "poke.release";
    private const string Drag = "drag";

    private enum Overlay { None, Press, Release, Drag }

    private sealed class Track(AssetAnimation animation)
    {
        public AssetAnimation Animation { get; set; } = animation;
        public double Elapsed { get; set; }
        public int Index { get; set; }
        public bool Finished { get; set; }
    }

    private readonly IReadOnlyDictionary<string, AssetAnimation> _animations;
    private Track _base;
    private Track? _overlay;
    private Overlay _overlayKind = Overlay.None;
    private SpriteState _state = SpriteState.Idle;

    public SpriteAnimator(IReadOnlyDictionary<string, AssetAnimation> animations)
    {
        if (!animations.ContainsKey(Idle))
            throw new ArgumentException("素材型角色必須有 idle 動畫。", nameof(animations));
        _animations = animations;
        _base = new Track(animations[Idle]);
    }

    private Track Visible => _overlay ?? _base;

    public AnimationFrame Current => new(Visible.Animation.Key, Visible.Index);

    /// <summary>目前播放的動畫的 fps。</summary>
    public double Fps => Visible.Animation.Fps;

    /// <summary>畫面已經停住，除非有狀態或互動事件否則不會再變（殼可以把幀率降到最低）。</summary>
    public bool IsStatic => Visible.Finished || Visible.Animation.Frames == 1;

    public bool HasOverlay => _overlay is not null;

    public SpriteState State => _state;

    /// <summary>設定狀態：從該狀態動畫的第 0 格重新開始；同一個狀態重複設定不重來。沒有這個狀態的動畫就用 idle。</summary>
    public void SetState(SpriteState state)
    {
        if (state == _state)
            return;
        _state = state;
        _base = new Track(_animations.TryGetValue(KeyOf(state), out var animation) ? animation : _animations[Idle]);
    }

    public void OnInteraction(SpriteInteraction e)
    {
        switch (e)
        {
            case SpriteInteraction.PokeBegin when _overlayKind != Overlay.Drag:
                if (_animations.TryGetValue(PokePress, out var press))
                    StartOverlay(Overlay.Press, press);
                break;
            case SpriteInteraction.PokeEnd when _overlayKind is Overlay.Press:
                if (_animations.TryGetValue(PokeRelease, out var release))
                    StartOverlay(Overlay.Release, release);
                else
                    ClearOverlay();
                break;
            case SpriteInteraction.DragBegin:
                if (_animations.TryGetValue(Drag, out var drag))
                    StartOverlay(Overlay.Drag, drag);
                else
                    ClearOverlay();
                break;
            case SpriteInteraction.DragEnd when _overlayKind is Overlay.Drag:
                ClearOverlay();
                break;
        }
    }

    public void Advance(TimeSpan dt)
    {
        var seconds = Math.Clamp(dt.TotalSeconds, 0, MaxStep.TotalSeconds);
        if (seconds <= 0)
            return;

        if (_overlay is { } overlay)
        {
            if (Step(overlay, seconds) && _overlayKind == Overlay.Release)
                ClearOverlay();   // 戳放開的動畫播完：回到底層目前的狀態
            return;   // 覆蓋期間底層不動，結束後從原來的位置繼續
        }
        StepBase(seconds);
    }

    // ---- 內部 ----

    private void StartOverlay(Overlay kind, AssetAnimation animation)
    {
        _overlayKind = kind;
        _overlay = new Track(animation);
    }

    private void ClearOverlay()
    {
        _overlay = null;
        _overlayKind = Overlay.None;
    }

    private static string KeyOf(SpriteState state) => state.ToString().ToLowerInvariant();

    /// <summary>推進一個軌道。回傳 true 表示它（不循環的）播完了；循環的永遠回 false。</summary>
    private static bool Step(Track track, double seconds)
    {
        if (track.Finished)
            return true;

        var duration = 1.0 / track.Animation.Fps;
        track.Elapsed += seconds;
        var steps = (int)(track.Elapsed / duration);
        if (steps <= 0)
            return false;

        track.Elapsed -= steps * duration;
        var index = track.Index + steps;
        var frames = track.Animation.Frames;
        if (index < frames)
        {
            track.Index = index;
            return false;
        }
        if (track.Animation.Loop)
        {
            track.Index = index % frames;
            return false;
        }

        track.Index = frames - 1;
        track.Finished = true;
        return true;
    }

    private void StepBase(double seconds)
    {
        if (!Step(_base, seconds))
            return;

        // 不循環的動畫播完：接 next；沒有 next 就回 idle（idle 本身停在最後一格）。
        // 接上的動畫從頭開始，這次推進剩下的時間不往下算（所以 next 互指的怪 manifest 也不會在一次推進裡繞圈）
        var animation = _base.Animation;
        if (animation.Next is { } next && _animations.TryGetValue(next, out var named))
            _base = new Track(named);
        else if (!animation.HoldLast && animation.Key != Idle)
            _base = new Track(_animations[Idle]);
    }
}
