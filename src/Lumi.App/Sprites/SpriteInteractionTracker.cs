using Avalonia;

namespace Lumi.App.Sprites;

/// <summary>追蹤器對殼說的話。角色收到的 <see cref="Lumi.Sprites.Abstractions.SpriteInteraction"/> 另外由殼轉換。</summary>
public abstract record TrackerEvent
{
    public sealed record NearChanged(bool IsNear) : TrackerEvent;

    /// <summary>游標方向，兩個分量都在 -1..1。</summary>
    public sealed record Looking(Vector Direction) : TrackerEvent;

    /// <summary>短按放開，沒有戳也沒有拖。</summary>
    public sealed record Clicked : TrackerEvent;

    public sealed record PokeBegan(Point At) : TrackerEvent;

    public sealed record PokeEnded : TrackerEvent;

    public sealed record DragBegan : TrackerEvent;

    /// <summary>拖曳中，相對於按下那一刻的游標位移（DIP）。</summary>
    public sealed record DragMoved(Vector Delta) : TrackerEvent;

    public sealed record DragEnded : TrackerEvent;
}

/// <summary>
/// 靠近、離開延遲、長按戳、拖曳門檻的純邏輯（吃座標與時間，吐事件），不碰視窗。
/// M0 在 Mac 與 Windows 手動驗證過的規則，現在有單元測試：
/// 戳住後不會變拖曳、短按後立刻拖是拖曳、長按不動是戳、拖曳不會同時算點擊。
/// </summary>
public sealed class SpriteInteractionTracker(SpriteShellOptions options, TimeProvider time)
{
    private const double LookEpsilon = 0.01;

    private bool _near;
    private DateTimeOffset _lastNearAt = DateTimeOffset.MinValue;
    private Vector _look;

    private bool _pressed;
    private Point _pressPoint;
    private DateTimeOffset _pressedAt;

    public bool IsNear => _near;

    public bool IsPressed => _pressed;

    public bool IsPoking { get; private set; }

    public bool IsDragging { get; private set; }

    public event Action<TrackerEvent>? Raised;

    /// <summary>
    /// 每個輪詢週期呼叫一次（也推進長按的計時）。
    /// </summary>
    /// <param name="cursorFromCenter">游標相對角色中心的位置（DIP）；取不到（原生不支援）時為 null。</param>
    /// <param name="nearRadius">靠近半徑（DIP）。</param>
    /// <param name="keepNear">游標在輸入列上、或正在打字等「雖然離角色遠但還在使用」的情況。</param>
    /// <param name="insideFallback">取不到游標時的退化：游標是否在視窗內（由視窗的 PointerEntered／Exited 提供）。</param>
    public void Poll(Vector? cursorFromCenter, double nearRadius, bool keepNear, bool insideFallback = false)
    {
        var now = time.GetUtcNow();
        bool rawNear;
        if (cursorFromCenter is { } d)
        {
            rawNear = Math.Sqrt(d.X * d.X + d.Y * d.Y) < nearRadius || keepNear;
            var look = new Vector(Math.Clamp(d.X / nearRadius, -1, 1), Math.Clamp(d.Y / nearRadius, -1, 1));
            if (Math.Abs(look.X - _look.X) > LookEpsilon || Math.Abs(look.Y - _look.Y) > LookEpsilon)
            {
                _look = look;
                Raised?.Invoke(new TrackerEvent.Looking(look));
            }
        }
        else
        {
            rawNear = insideFallback || keepNear;
        }

        if (rawNear || _pressed)
            _lastNearAt = now;
        SetNear(now - _lastNearAt < options.NearHold);

        // 長按：同一點按住超過門檻就是戳（拖曳開始後不會再戳）
        if (_pressed && !IsPoking && !IsDragging && now - _pressedAt >= options.LongPress)
        {
            IsPoking = true;
            Raised?.Invoke(new TrackerEvent.PokeBegan(_pressPoint));
        }
    }

    private void SetNear(bool near)
    {
        if (near == _near)
            return;
        _near = near;
        Raised?.Invoke(new TrackerEvent.NearChanged(near));
    }

    /// <param name="atSprite">按下的位置（角色 <c>Visual</c> 的座標）。呼叫端先用 <c>HitTest</c> 確認真的按在角色上。</param>
    public void PointerPressed(Point atSprite)
    {
        _pressed = true;
        IsPoking = false;
        IsDragging = false;
        _pressPoint = atSprite;
        _pressedAt = time.GetUtcNow();
    }

    /// <param name="delta">相對於按下那一刻的游標位移（DIP，螢幕座標）。</param>
    public void PointerMoved(Vector delta)
    {
        if (!_pressed)
            return;

        if (!IsDragging)
        {
            if (IsPoking)
                return;   // 已經戳住就不轉成拖曳
            if (Math.Abs(delta.X) < options.DragThreshold && Math.Abs(delta.Y) < options.DragThreshold)
                return;

            IsDragging = true;
            Raised?.Invoke(new TrackerEvent.DragBegan());
        }
        Raised?.Invoke(new TrackerEvent.DragMoved(delta));
    }

    public void PointerReleased()
    {
        if (!_pressed)
            return;

        var wasDragging = IsDragging;
        var wasPoking = IsPoking;
        Reset();
        if (wasDragging)
            Raised?.Invoke(new TrackerEvent.DragEnded());
        else if (wasPoking)
            Raised?.Invoke(new TrackerEvent.PokeEnded());
        else
            Raised?.Invoke(new TrackerEvent.Clicked());
    }

    /// <summary>指標被搶走（例如開了選單）：結束拖曳或戳，但不算點擊。</summary>
    public void CaptureLost()
    {
        if (!_pressed)
            return;

        var wasDragging = IsDragging;
        var wasPoking = IsPoking;
        Reset();
        if (wasDragging)
            Raised?.Invoke(new TrackerEvent.DragEnded());
        if (wasPoking)
            Raised?.Invoke(new TrackerEvent.PokeEnded());
    }

    private void Reset()
    {
        _pressed = false;
        IsPoking = false;
        IsDragging = false;
    }
}
