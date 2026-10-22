namespace Lumi.Core.Speech;

/// <summary>
/// 麥克風錄音（16 kHz／單聲道／16-bit PCM）。平台實作在 Lumi.Platform（Windows waveIn、macOS AudioQueue）。
/// 一次只錄一個；錄音只存在記憶體，不寫磁碟。
/// </summary>
public interface IAudioRecorder
{
    bool IsRecording { get; }

    /// <summary>音量 0..1，約每 50 ms 一次。在原生的背景執行緒發出，觀察者自己轉到 UI 執行緒。</summary>
    event EventHandler<float>? LevelChanged;

    /// <summary>達到 <see cref="RecordingLimits.MaxDuration"/> 時發出一次。錄音器停止收新資料，但仍要等 <see cref="StopAsync"/> 取結果。</summary>
    event EventHandler? MaxDurationReached;

    /// <summary>
    /// 開始錄音。麥克風沒權限、沒有裝置、被其他程式獨佔等：丟 <see cref="AudioRecorderException"/>（Kind + 可直接顯示的繁中訊息）。
    /// 已經在錄音時丟 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>停止並回傳 WAV（含檔頭，記憶體）。時間不足 <see cref="RecordingLimits.MinDuration"/> 時 <see cref="RecordedAudio.TooShort"/> 為 true。</summary>
    Task<RecordedAudio> StopAsync();

    /// <summary>停止並丟掉錄到的內容。沒有在錄音時不做任何事。</summary>
    void Cancel();
}

/// <param name="Wav">16 kHz／單聲道／16-bit 的 WAV（含檔頭）。</param>
/// <param name="Duration">錄到的長度（由取樣數算出）。</param>
/// <param name="TooShort">短於 <see cref="RecordingLimits.MinDuration"/>，不應送出。</param>
/// <param name="LooksSilent">整段幾乎沒有聲音（RMS 低於 <see cref="RecordingLimits.SilentRmsThreshold"/>），多半是沒有麥克風權限或麥克風沒有收音。</param>
public sealed record RecordedAudio(byte[] Wav, TimeSpan Duration, bool TooShort, bool LooksSilent);
