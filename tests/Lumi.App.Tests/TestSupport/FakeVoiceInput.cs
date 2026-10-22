using System.ComponentModel;
using Lumi.App.Resources;
using Lumi.App.Services;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 假的語音輸入控制器：狀態、原因、提示都可以直接設定，<see cref="Calls"/> 記錄命令，<see cref="RaiseTranscribed"/> 模擬轉寫完成。
/// 給工作台與畫面測試用（真正的狀態機在 <c>VoiceInputControllerTests</c>）。
/// </summary>
internal sealed class FakeVoiceInput : IVoiceInputController
{
    private VoiceInputState _state;
    private string? _notice;

    public List<string> Calls { get; } = [];

    public VoiceInputState State
    {
        get => _state;
        set
        {
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    public TimeSpan Elapsed { get; set; }

    public TimeSpan? RecordedDuration { get; set; }

    public double Level { get; set; }

    public string? LastFailureMessage { get; set; }

    public string? Notice
    {
        get => _notice;
        set
        {
            _notice = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Notice)));
        }
    }

    public bool CanStart { get; set; } = true;

    public string? DisabledReason { get; set; }

    public VoiceOrigin? Origin { get; set; }

    /// <summary>和真正的控制器相同：另一邊錄音或轉寫中時原因是「另一個視窗正在語音輸入」。</summary>
    public string? DisabledReasonFor(VoiceOrigin origin) =>
        (State is VoiceInputState.Recording or VoiceInputState.Transcribing) && Origin is { } owner && owner != origin
            ? Strings.Voice_OtherWindow
            : DisabledReason;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<VoiceTranscribedArgs>? Transcribed;

    public Task StartAsync(VoiceOrigin origin, CancellationToken ct = default)
    {
        Origin = origin;
        Calls.Add("start");
        return Task.CompletedTask;
    }

    public Task StopAsync(VoiceStopAction action)
    {
        Calls.Add("stop:" + action);
        return Task.CompletedTask;
    }

    public void Cancel() => Calls.Add("cancel");

    public Task RetryAsync()
    {
        Calls.Add("retry");
        return Task.CompletedTask;
    }

    public void Discard() => Calls.Add("discard");

    public void ClearNotice() => Notice = null;

    /// <summary>模擬轉寫完成（在 UI 執行緒上發出，狀態應該已經是 Idle）。</summary>
    public void RaiseTranscribed(VoiceTranscribedArgs args) => Transcribed?.Invoke(this, args);

    /// <summary>模擬計時的更新（發出 Elapsed 的變更通知）。</summary>
    public void SetElapsed(TimeSpan elapsed)
    {
        Elapsed = elapsed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Elapsed)));
    }

    /// <summary>模擬一個新的音量樣本（會發出 Level 的變更通知，波形才會重繪）。</summary>
    public void SetLevel(double level)
    {
        Level = level;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Level)));
    }

    /// <summary>模擬「可以用／不能用」的變化。</summary>
    public void SetAvailability(bool canStart, string? reason)
    {
        CanStart = canStart;
        DisabledReason = reason;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisabledReason)));
    }
}
