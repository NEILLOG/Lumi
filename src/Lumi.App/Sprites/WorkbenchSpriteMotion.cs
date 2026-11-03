namespace Lumi.App.Sprites;

/// <summary>
/// 工作台角色的程式動作（M27，plan/27 第 8.3 節）：跳一下、瞬移、探頭。純邏輯，吃時間、吐偏移與透明度，不碰畫面。
/// 呼叫端每一幀呼叫 <see cref="Update"/>，再讀 <see cref="OffsetY"/>、<see cref="Opacity"/>。
/// </summary>
public sealed class WorkbenchSpriteMotion
{
    // ---- 時間常數（plan/27 第 8.3 節） ----

    /// <summary>探頭：完整下沉量冒出來要的時間（線性，從目前位置繼續）。</summary>
    private const double RiseMs = 200;

    /// <summary>探頭：完整下沉量縮回去要的時間（線性）。</summary>
    private const double SinkMs = 300;

    /// <summary>跳一下：0→−6 的時間點（90 ms）。</summary>
    private const double JumpUpMs = 90;

    /// <summary>跳一下：最高點的時間點（180 ms）。</summary>
    private const double JumpPeakMs = 180;

    /// <summary>跳一下：落地的時間點（360 ms）。</summary>
    private const double JumpEndMs = 360;

    /// <summary>跳一下：中途高度（−6，往上為負）。</summary>
    private const double JumpMidHeight = -6;

    /// <summary>跳一下：最高點（−10，往上為負）。</summary>
    private const double JumpPeakHeight = -10;

    /// <summary>瞬移：淡出的時間（0→120 ms 透明度 1→0），此時換位置。</summary>
    private const double TeleportFadeOutMs = 120;

    /// <summary>瞬移：結束的時間（120→300 ms 透明度 0→1）。</summary>
    private const double TeleportEndMs = 300;

    /// <summary>浮點數比較用的誤差。</summary>
    private const double Epsilon = 1e-9;

    private enum Movement { Still, Rising, Sinking }

    private readonly TimeProvider _time;

    /// <summary>上次 <see cref="Update"/> 的時間；<see cref="OffsetY"/> 與 <see cref="Opacity"/> 都以它為準。</summary>
    private DateTimeOffset _frameTime;

    /// <summary>探頭的完整下沉量（D）。</summary>
    private double _peekDepth;

    /// <summary>目前的下沉量（連續值）。</summary>
    private double _depth;

    private Movement _movement = Movement.Still;

    /// <summary>跳躍開始的時間；null 表示沒在跳。</summary>
    private DateTimeOffset? _jumpStart;

    /// <summary>已經要求跳，等冒完再跳。</summary>
    private bool _jumpPending;

    /// <summary>瞬移開始的時間；null 表示沒在瞬移。</summary>
    private DateTimeOffset? _teleportStart;

    /// <summary>瞬移中還沒叫過的換位置回呼；叫過就清成 null。</summary>
    private Action? _swap;

    public WorkbenchSpriteMotion(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _frameTime = time.GetUtcNow();
    }

    /// <summary>垂直偏移（整數 DIP，往下為正）：探頭下沉量加跳躍高度。</summary>
    public double OffsetY => Math.Round(_depth + JumpOffset(), MidpointRounding.AwayFromZero);

    public double Opacity
    {
        get
        {
            if (_teleportStart is not { } start)
                return 1;

            var t = Math.Max(0, (_frameTime - start).TotalMilliseconds);
            if (t < TeleportFadeOutMs)
                return 1 - t / TeleportFadeOutMs;
            if (t < TeleportEndMs)
                return (t - TeleportFadeOutMs) / (TeleportEndMs - TeleportFadeOutMs);
            return 1;
        }
    }

    /// <summary>有動作在進行：呼叫端要維持高幀率。</summary>
    public bool IsAnimating => _movement != Movement.Still || _jumpStart is not null || _jumpPending || _teleportStart is not null;

    /// <summary>瞬移進行中。</summary>
    public bool IsTeleporting => _teleportStart is not null;

    /// <summary>最後一次開始瞬移的時間；還沒瞬移過是 null。</summary>
    public DateTimeOffset? LastTeleportAt { get; private set; }

    /// <summary>探頭的完整下沉量。0＝這個位置沒有探頭（歡迎區）：立刻回到全身。</summary>
    public void SetPeekDepth(double depth)
    {
        var d = Math.Max(0, depth);
        if (d <= 0)
        {
            _peekDepth = 0;
            _depth = 0;
            _movement = Movement.Still;
            return;
        }

        // 原本已經沉到底、或正在下沉：維持下沉到新的 D（夾到新的 D 之內）
        var wasBottomed = _peekDepth > 0 && _movement == Movement.Still && _depth >= _peekDepth - Epsilon;
        var keepSinking = _movement == Movement.Sinking || wasBottomed;

        _peekDepth = d;
        if (_depth > d)
            _depth = d;
        if (keepSinking)
            BeginMovement(_depth < d - Epsilon ? Movement.Sinking : Movement.Still);
    }

    /// <summary>冒出來（線性，完整下沉量 200 ms）；還在沉就從目前位置往回。</summary>
    public void Rise()
    {
        if (_peekDepth <= 0 || _depth <= 0)
            return;
        BeginMovement(Movement.Rising);
    }

    /// <summary>縮回去（線性，完整下沉量 300 ms）。</summary>
    public void Sink()
    {
        if (_peekDepth <= 0 || _jumpStart is not null || _jumpPending)
            return;
        BeginMovement(_depth < _peekDepth - Epsilon ? Movement.Sinking : Movement.Still);
    }

    /// <summary>跳一下。跳躍中、瞬移中不理會；還在冒出來就等冒完才跳。</summary>
    public void Jump()
    {
        if (_teleportStart is not null || _jumpStart is not null || _jumpPending)
            return;

        // 已經沉著（正在冒出、沉到一半、沉到底）：先冒出來，冒完才跳
        if (_depth > 0)
        {
            Rise();
            _jumpPending = true;
            return;
        }

        StartJump(_time.GetUtcNow());
    }

    /// <summary>瞬移：淡出 120 ms 後呼叫 <paramref name="swap"/>（呼叫端在這裡換位置），再淡入到 300 ms。進行中再呼叫不理會。</summary>
    public void BeginTeleport(Action swap)
    {
        ArgumentNullException.ThrowIfNull(swap);
        if (_teleportStart is not null)
            return;

        var now = _time.GetUtcNow();
        _teleportStart = now;
        LastTeleportAt = now;
        _swap = swap;

        // 瞬移時取消跳躍（含等待的跳躍）
        _jumpStart = null;
        _jumpPending = false;
    }

    /// <summary>推進到現在的時間。</summary>
    public void Update()
    {
        var now = _time.GetUtcNow();
        var elapsedMs = Math.Max(0, (now - _frameTime).TotalMilliseconds);
        _frameTime = now;

        switch (_movement)
        {
            case Movement.Rising:
                _depth -= _peekDepth / RiseMs * elapsedMs;
                if (_depth <= Epsilon)
                {
                    _depth = 0;
                    _movement = Movement.Still;
                    // 冒完才跳：這一刻開始跳
                    if (_jumpPending)
                        StartJump(now);
                }
                break;

            case Movement.Sinking:
                _depth += _peekDepth / SinkMs * elapsedMs;
                if (_depth >= _peekDepth - Epsilon)
                {
                    _depth = _peekDepth;
                    _movement = Movement.Still;
                }
                break;
        }

        if (_teleportStart is { } start)
        {
            var t = (now - start).TotalMilliseconds;
            if (t >= TeleportFadeOutMs && _swap is { } swap)
            {
                _swap = null;
                swap();
            }
            if (t >= TeleportEndMs)
                _teleportStart = null;
        }

        if (_jumpStart is { } jumpStart && (now - jumpStart).TotalMilliseconds >= JumpEndMs)
            _jumpStart = null;
    }

    /// <summary>取消所有進行中的動作，回到全身、不透明（視窗隱藏、關閉功能時）。還沒換位置的瞬移，<c>swap</c> 會立刻被呼叫。</summary>
    public void Reset()
    {
        var swap = _swap;
        _swap = null;
        _teleportStart = null;
        swap?.Invoke();

        _jumpStart = null;
        _jumpPending = false;
        _depth = 0;
        _movement = Movement.Still;
    }

    /// <summary>開始動：從靜止開始時把時鐘拉到現在，靜止時沒有時間該算進下沉量（畫面沒呼叫 Update 的空檔不會被一次推完）。</summary>
    private void BeginMovement(Movement movement)
    {
        if (_movement == Movement.Still)
            _frameTime = _time.GetUtcNow();
        _movement = movement;
    }

    private void StartJump(DateTimeOffset at)
    {
        _jumpStart = at;
        _jumpPending = false;
    }

    /// <summary>跳躍偏移（往上為負）：0→−6（0～90 ms）、−6→−10（90～180 ms）、−10→0（180～360 ms）、之後 0。</summary>
    private double JumpOffset()
    {
        if (_jumpStart is not { } start)
            return 0;

        // 開始跳的那一刻還沒有 Update 時，時間可能是負的
        var t = Math.Max(0, (_frameTime - start).TotalMilliseconds);
        if (t < JumpUpMs)
            return JumpMidHeight * t / JumpUpMs;
        if (t < JumpPeakMs)
            return JumpMidHeight + (JumpPeakHeight - JumpMidHeight) * (t - JumpUpMs) / (JumpPeakMs - JumpUpMs);
        if (t < JumpEndMs)
            return JumpPeakHeight * (1 - (t - JumpPeakMs) / (JumpEndMs - JumpPeakMs));
        return 0;
    }
}
