using Lumi.Core.Speech;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 假的錄音器：測試可手動觸發音量與達上限事件，並指定 Start／Stop 的結果或失敗。
/// 錄音狀態行為與平台實作一致（一次只錄一個，沒在錄音時 Stop 丟 InvalidOperationException）。
/// </summary>
internal sealed class FakeAudioRecorder : IAudioRecorder
{
    public bool IsRecording { get; private set; }

    public event EventHandler<float>? LevelChanged;

    public event EventHandler? MaxDurationReached;

    /// <summary>StartAsync 要丟的例外（null＝成功）。</summary>
    public AudioRecorderException? StartFailure { get; set; }

    /// <summary>StopAsync 要丟的例外（null＝成功）。</summary>
    public AudioRecorderException? StopFailure { get; set; }

    /// <summary>StopAsync 回傳的結果；預設為 1 秒靜音。</summary>
    public RecordedAudio StopResult { get; set; } = new(
        Lumi.Core.Speech.WavFormat.Silence(TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(1), TooShort: false, LooksSilent: true);

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int CancelCount { get; private set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRecording)
            throw new InvalidOperationException("已經在錄音中。");
        ct.ThrowIfCancellationRequested();

        StartCount++;
        if (StartFailure is not null)
            throw StartFailure;

        IsRecording = true;
        return Task.CompletedTask;
    }

    public Task<RecordedAudio> StopAsync()
    {
        if (!IsRecording)
            throw new InvalidOperationException("目前沒有在錄音。");

        StopCount++;
        IsRecording = false;
        if (StopFailure is not null)
            throw StopFailure;

        return Task.FromResult(StopResult);
    }

    public void Cancel()
    {
        CancelCount++;
        IsRecording = false;
    }

    /// <summary>模擬一段音量（0..1）。</summary>
    public void RaiseLevel(float level) => LevelChanged?.Invoke(this, level);

    /// <summary>模擬達到 90 秒上限。</summary>
    public void RaiseMaxDurationReached() => MaxDurationReached?.Invoke(this, EventArgs.Empty);
}
