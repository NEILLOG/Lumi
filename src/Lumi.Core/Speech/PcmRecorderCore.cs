namespace Lumi.Core.Speech;

/// <summary>
/// 平台無關的錄音累積器：平台錄音器把原生回呼收到的 PCM 餵進來，這裡負責累積、算音量、套用時間上限，
/// 最後產生 <see cref="RecordedAudio"/>。執行緒安全（原生回呼與 StopAsync 在不同執行緒）。
/// </summary>
public sealed class PcmRecorderCore
{
    private readonly object _gate = new();
    private readonly MemoryStream _pcm = new();
    private double _sumSquares;
    private long _samples;
    private bool _reachedMax;
    private bool _completed;

    /// <summary>一次 <see cref="Append"/> 的結果。</summary>
    /// <param name="Level">這一段資料的音量 0..1。</param>
    /// <param name="MaxReachedNow">這一次呼叫讓錄音達到上限（只有一次為 true）。</param>
    public readonly record struct AppendResult(float Level, bool MaxReachedNow);

    /// <summary>已累積的長度（達上限後不再增加）。</summary>
    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
                return TimeSpan.FromSeconds(_samples / (double)WavFormat.SampleRate);
        }
    }

    /// <summary>已達 <see cref="RecordingLimits.MaxDuration"/>；之後的資料全部忽略。</summary>
    public bool ReachedMax
    {
        get
        {
            lock (_gate)
                return _reachedMax;
        }
    }

    /// <summary>
    /// 累積一段 PCM16 小端序資料。達上限時只收到上限為止，並回報 <c>MaxReachedNow</c>；
    /// 上限之後與 <see cref="Complete"/> 之後的資料都忽略（回 Level 0）。
    /// 不丟例外，因為會從原生回呼呼叫。
    /// </summary>
    public AppendResult Append(ReadOnlySpan<byte> pcm)
    {
        lock (_gate)
        {
            if (_completed || _reachedMax)
                return new AppendResult(0, false);

            // 取樣必須成對，多出的一個位元組丟棄（原生緩衝大小本來就是偶數）
            pcm = pcm[..(pcm.Length & ~1)];

            var reachedNow = false;
            var room = RecordingLimits.MaxPcmBytes - (int)_pcm.Length;
            if (pcm.Length >= room)
            {
                pcm = pcm[..room];
                reachedNow = true;
                _reachedMax = true;
            }

            var sum = LevelMeter.SumSquares(pcm, out var count);
            _pcm.Write(pcm);
            _sumSquares += sum;
            _samples += count;

            var level = LevelMeter.Level(count == 0 ? 0 : Math.Sqrt(sum / count));
            return new AppendResult(level, reachedNow);
        }
    }

    /// <summary>結束錄音並產生 WAV。只能呼叫一次。</summary>
    public RecordedAudio Complete()
    {
        lock (_gate)
        {
            if (_completed)
                throw new InvalidOperationException("錄音已經結束。");
            _completed = true;

            var duration = TimeSpan.FromSeconds(_samples / (double)WavFormat.SampleRate);
            var wav = WavFormat.FromPcm(_pcm.GetBuffer().AsSpan(0, (int)_pcm.Length));
            var rms = _samples == 0 ? 0 : Math.Sqrt(_sumSquares / _samples);

            return new RecordedAudio(
                wav,
                duration,
                TooShort: duration < RecordingLimits.MinDuration,
                LooksSilent: _samples > 0 && rms < RecordingLimits.SilentRmsThreshold);
        }
    }
}
