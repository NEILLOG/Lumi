using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 工作台角色的對話接線（M27，plan/27 第 8.3 節）：<see cref="IChatSession"/> → <see cref="SpriteStateMapper"/>（自己的實例，不和小精靈視窗共用）
/// → <see cref="SpriteSleepPolicy"/>，並決定何時跳、何時探頭。不碰 Avalonia；角色與版面在 <c>WorkbenchSpritePresenter</c>。
/// </summary>
public sealed class WorkbenchSpriteController : IDisposable
{
    /// <summary>瞬移後這段時間內，送出的回應不跳（plan/27 第 8.3 節：瞬移那一次不跳）。</summary>
    private static readonly TimeSpan TeleportJumpCooldown = TimeSpan.FromSeconds(1);

    private readonly IChatSession _session;
    private readonly IToolApprovalResponder? _approvals;
    private readonly TimeProvider _time;
    private readonly SpriteStateMapper _mapper;
    private readonly SpriteSleepPolicy _sleep;
    private readonly WorkbenchSpriteMotion _motion;

    private bool _attached;
    private bool _near;
    private WorkbenchSpriteSlot _slot = WorkbenchSpriteSlot.None;
    private DateTimeOffset _idleSince;
    private SpriteState _state = SpriteState.Idle;

    public WorkbenchSpriteController(IChatSession session, IToolApprovalResponder? approvals, SpriteShellOptions shell, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(time);

        _session = session;
        _approvals = approvals;
        _time = time;
        _mapper = new SpriteStateMapper(shell, time);
        _sleep = new SpriteSleepPolicy(shell, time);
        _motion = new WorkbenchSpriteMotion(time);
        _idleSince = time.GetUtcNow();

        _mapper.Changed += OnMapperChanged;
    }

    public WorkbenchSpriteMotion Motion => _motion;

    /// <summary>角色現在該呈現的狀態（已疊上休眠，還沒依角色支援解析退化）。</summary>
    public SpriteState State => _state;

    /// <summary><see cref="State"/> 改變。</summary>
    public event Action<SpriteState>? StateChanged;

    /// <summary>目前版位。只有 <see cref="WorkbenchSpriteSlot.Toolbar"/> 才會跳與探頭。</summary>
    public WorkbenchSpriteSlot Slot
    {
        get => _slot;
        set
        {
            if (value == _slot)
                return;
            _slot = value;
            ResetIdle();
            if (value != WorkbenchSpriteSlot.Toolbar)
                _motion.SetPeekDepth(0);
        }
    }

    /// <summary>開始接收對話事件。已經在回應中就直接進思考。重複呼叫沒有作用。</summary>
    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;

        _session.RunStarted += OnRunStarted;
        _session.EventReceived += OnEvent;
        _session.RunFinished += OnRunFinished;
        _session.ConversationChanged += OnConversationChanged;
        if (_approvals is not null)
            _approvals.Resolved += OnApprovalResolved;

        if (_session.IsResponding)
            _mapper.OnRunStarted();

        ResetIdle();
        Recalculate();
    }

    /// <summary>停止接收對話事件（功能關閉）。</summary>
    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;

        _session.RunStarted -= OnRunStarted;
        _session.EventReceived -= OnEvent;
        _session.RunFinished -= OnRunFinished;
        _session.ConversationChanged -= OnConversationChanged;
        if (_approvals is not null)
            _approvals.Resolved -= OnApprovalResolved;
    }

    /// <summary>游標靠近或離開（由殼的 <see cref="SpriteInteractionTracker"/> 提供）。</summary>
    public void SetNear(bool near)
    {
        _near = near;
        Recalculate();
        if (near && _slot == WorkbenchSpriteSlot.Toolbar)
        {
            _motion.Rise();
            ResetIdle();
        }
    }

    /// <summary>使用者碰了角色：重新計時休眠。</summary>
    public void Touch()
    {
        _sleep.Touch();
        ResetIdle();
        if (_slot == WorkbenchSpriteSlot.Toolbar)
            _motion.Rise();
        Recalculate();
    }

    /// <summary>每個輪詢週期呼叫：完成狀態回待機、休眠、探頭。</summary>
    public void Tick()
    {
        _mapper.Tick();
        Recalculate();

        if (_slot != WorkbenchSpriteSlot.Toolbar)
            return;

        if (_mapper.State != SpriteState.Idle || _near)
        {
            ResetIdle();
            _motion.Rise();
        }
        else if (_time.GetUtcNow() - _idleSince >= WorkbenchSpriteRules.PeekDelay)
        {
            _motion.Sink();
        }
    }

    public void Dispose() => Detach();

    // ---- 對話事件 ----

    private void OnRunStarted(object? sender, RunStartedArgs args)
    {
        _mapper.OnRunStarted();

        // 不濾模式（plan/27 D7）：只有工具列上、使用者送出的訊息跳；重試與重新產生不跳，瞬移剛結束也不跳
        if (args.Kind == ChatRunKind.Send && _slot == WorkbenchSpriteSlot.Toolbar && CanJumpAfterTeleport())
            _motion.Jump();

        Recalculate();
    }

    private void OnEvent(object? sender, ChatEvent chatEvent)
    {
        _mapper.OnEvent(chatEvent);
        Recalculate();
    }

    private void OnRunFinished(object? sender, RunFinishedArgs args)
    {
        _mapper.OnRunFinished(args.Result);
        Recalculate();
    }

    private void OnConversationChanged(object? sender, ConversationChangedArgs args)
    {
        // 工作台的錯誤列沒有關閉鈕，換對話就離開 Error（plan/27 第 8.1 節第 8 項）
        _mapper.Acknowledge();
        Recalculate();
    }

    private void OnApprovalResolved(object? sender, ApprovalResolved resolved)
    {
        _mapper.OnApprovalResolved(resolved.CallId);
        Recalculate();
    }

    private void OnMapperChanged(SpriteState state)
    {
        if (state == SpriteState.Done && _slot == WorkbenchSpriteSlot.Toolbar)
            _motion.Jump();
        Recalculate();
    }

    // ---- 內部 ----

    private bool CanJumpAfterTeleport() =>
        !_motion.IsTeleporting && (_motion.LastTeleportAt is not { } last || _time.GetUtcNow() - last >= TeleportJumpCooldown);

    private void ResetIdle() => _idleSince = _time.GetUtcNow();

    /// <summary>把 mapper 的狀態疊上休眠，改變時才發事件。</summary>
    private void Recalculate()
    {
        var next = _sleep.Update(_mapper.State, _near);
        if (next == _state)
            return;
        _state = next;
        StateChanged?.Invoke(next);
    }
}
