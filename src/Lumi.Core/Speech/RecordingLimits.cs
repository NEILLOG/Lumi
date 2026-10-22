namespace Lumi.Core.Speech;

/// <summary>錄音的時間與靜音判定常數（集中在這裡，之後可調）。</summary>
public static class RecordingLimits
{
    /// <summary>錄音上限；到了自動停止收新資料並發出 <see cref="IAudioRecorder.MaxDurationReached"/>。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(90);

    /// <summary>最短錄音長度；比這短的錄音不送出。</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(0.5);

    /// <summary>整段錄音的 RMS（0..1）低於此值視為沒有聲音（約 -60 dBFS）。</summary>
    public const double SilentRmsThreshold = 0.001;

    /// <summary>上限換算成 PCM 位元組數（16 kHz／單聲道／16-bit 約 2.8 MB）。</summary>
    public static readonly int MaxPcmBytes = (int)Math.Round(MaxDuration.TotalSeconds * WavFormat.BytesPerSecond);
}
