using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 對話事件 → 角色狀態（純邏輯，計時用 <see cref="TimeProvider"/>）。<b>角色不知道對話</b>：它只會收到
/// 「現在是思考中」這類訊號，由殼把這裡的結果交給角色（自動休眠與退化規則由殼另外處理）。
/// <para>
/// 語音輸入（M6 6E）：<c>Recording → Listening</c>、<c>Transcribing → Transcribing</c>、<c>Failed → Error</c>
/// （失敗是 API 失敗一類，光球的參考表現是紅色搖頭，停到被關掉為止），轉寫完成回到 <c>Idle</c>。
/// 小精靈這一輪回應進行中時語音不蓋過對話狀態（<see cref="OnVoiceState"/>）。
/// </para>
/// <para>
/// <c>RunStarted → Thinking</c>、<c>ThinkingDelta → Thinking</c>、<c>TextDelta → Streaming</c>、
/// <c>ToolCallStarted → Tool</c>、<c>ApprovalRequired → Attention</c>（被回答後回 <c>Tool</c>）、
/// <c>ToolCallCompleted → Thinking</c>（等模型根據結果回應）、<c>Completed</c>（不是 <c>ToolUse</c>）→ <c>Done</c>
/// 並在數秒後回 <c>Idle</c>、失敗 → <c>Error</c>（停到被確認或下一輪開始）、使用者停止 → <c>Idle</c>。
/// </para>
/// </summary>
public sealed class SpriteStateMapper(SpriteShellOptions options, TimeProvider time)
{
    private DateTimeOffset _doneAt;
    private string? _awaitingCallId;
    private bool _running;
    private bool _voiceDriven;

    public SpriteState State { get; private set; } = SpriteState.Idle;

    public event Action<SpriteState>? Changed;

    public void OnRunStarted()
    {
        _running = true;
        _voiceDriven = false;
        _awaitingCallId = null;
        Set(SpriteState.Thinking);
    }

    /// <summary>語音輸入的狀態改變（由小精靈的語音接線呼叫）。回應進行中忽略（對話優先）；轉寫完成（Idle）只收回自己設定的狀態。</summary>
    public void OnVoiceState(VoiceInputState voice)
    {
        if (_running)
            return;

        switch (voice)
        {
            case VoiceInputState.Recording:
                _voiceDriven = true;
                Set(SpriteState.Listening);
                break;
            case VoiceInputState.Transcribing:
                _voiceDriven = true;
                Set(SpriteState.Transcribing);
                break;
            case VoiceInputState.Failed:
                _voiceDriven = true;
                Set(SpriteState.Error);
                break;
            case VoiceInputState.Idle:
                if (!_voiceDriven)
                    break;
                _voiceDriven = false;
                if (State is SpriteState.Listening or SpriteState.Transcribing or SpriteState.Error)
                    Set(SpriteState.Idle);
                break;
        }
    }

    public void OnEvent(ChatEvent chatEvent)
    {
        switch (chatEvent)
        {
            case ThinkingDelta when _awaitingCallId is null:
                Set(SpriteState.Thinking);
                break;
            case TextDelta:
                Set(SpriteState.Streaming);
                break;
            case ToolCallStarted:
                Set(SpriteState.Tool);
                break;
            case ApprovalRequired approval:
                _awaitingCallId = approval.Request.CallId;
                Set(SpriteState.Attention);
                break;
            case ToolCallCompleted:
                _awaitingCallId = null;
                Set(SpriteState.Thinking);
                break;
            case Completed { Reason: StopReason.ToolUse }:
                break;   // 這一輪還沒結束
            case Completed:
                _doneAt = time.GetUtcNow();
                Set(SpriteState.Done);
                break;
            case Failed:
                Set(SpriteState.Error);
                break;
        }
    }

    /// <summary>權限確認被回答（工作台或氣泡都一樣）：回到 <c>Tool</c>（拒絕時模型接著回答，馬上會變成 Thinking／Streaming）。</summary>
    public void OnApprovalResolved(string callId)
    {
        if (_awaitingCallId != callId)
            return;
        _awaitingCallId = null;
        if (State == SpriteState.Attention)
            Set(SpriteState.Tool);
    }

    public void OnRunFinished(ChatRunResult result)
    {
        _running = false;
        _awaitingCallId = null;
        if (result.Unexpected || result.Failure is not null)
            Set(SpriteState.Error);
        else if (result.Cancelled || State is not SpriteState.Done)
            Set(SpriteState.Idle);   // 使用者停止，或串流沒有正常結束
    }

    /// <summary>錯誤的氣泡被關掉、或使用者再次互動：離開 <c>Error</c>。</summary>
    public void Acknowledge()
    {
        if (State == SpriteState.Error)
            Set(SpriteState.Idle);
    }

    /// <summary>每個輪詢週期呼叫：<c>Done</c> 停一下就回 <c>Idle</c>。</summary>
    public void Tick()
    {
        if (State == SpriteState.Done && time.GetUtcNow() - _doneAt >= options.DoneHold)
            Set(SpriteState.Idle);
    }

    private void Set(SpriteState state)
    {
        if (state == State)
            return;
        State = state;
        Changed?.Invoke(state);
    }
}
