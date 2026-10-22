using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumi.Core.Speech;
using Lumi.Platform.Interop.Mac;
using static Lumi.Core.Speech.AudioRecorderFailureKind;

namespace Lumi.Platform.Recording;

/// <summary>
/// macOS 錄音：AudioToolbox <c>AudioQueue</c>（線性 PCM，16 kHz／單聲道／16-bit）。
/// 四個 50 ms 的緩衝；回呼把資料複製給 <see cref="PcmRecorderCore"/> 後重新排入。
/// 權限：Denied／Restricted 直接丟 PermissionDenied；NotDetermined 不先要求（見實作紀錄 6C），直接開始收音，由系統彈出授權視窗，
/// 這段期間收到的靜音由 <see cref="RecordedAudio.LooksSilent"/> 提示。
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacAudioQueueRecorder : IAudioRecorder, IDisposable
{
    private const int BufferCount = 4;
    private const int BufferBytes = WavFormat.BytesPerSecond / 20; // 50 ms

    private const string NoDeviceMessage =
        "找不到可用的麥克風。請確認麥克風已連接，並在「系統設定 › 隱私權與安全性 › 麥克風」允許 Lumi 使用麥克風。";

    private const string PermissionDeniedMessage =
        "沒有麥克風權限。請到「系統設定 › 隱私權與安全性 › 麥克風」開啟 Lumi（若從終端機或開發工具啟動，請開啟該程式），然後再試一次。";

    private readonly object _gate = new();
    private nint _queue;
    private PcmRecorderCore? _core;
    private GCHandle _self;
    private bool _recording;
    private volatile bool _stopping;

    public event EventHandler<float>? LevelChanged;

    public event EventHandler? MaxDurationReached;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
                return _recording;
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_recording)
                throw new InvalidOperationException("已經在錄音中。");

            var status = AVFoundation.AudioAuthorizationStatus();
            if (status is AVFoundation.Denied or AVFoundation.Restricted)
                throw new AudioRecorderException(PermissionDenied, PermissionDeniedMessage);

            var format = new AudioToolbox.AudioStreamBasicDescription
            {
                SampleRate = WavFormat.SampleRate,
                FormatId = AudioToolbox.KAudioFormatLinearPCM,
                FormatFlags = AudioToolbox.KAudioFormatFlagsPcm16,
                BytesPerPacket = WavFormat.BlockAlign,
                FramesPerPacket = 1,
                BytesPerFrame = WavFormat.BlockAlign,
                ChannelsPerFrame = WavFormat.Channels,
                BitsPerChannel = WavFormat.BitsPerSample,
            };

            // 回呼可能在 AudioQueueStart 之後立刻到來，所以狀態要在開始之前就設好
            var core = new PcmRecorderCore();
            var self = GCHandle.Alloc(this);
            _core = core;
            _self = self;
            _stopping = false;

            var created = AudioToolbox.AudioQueueNewInput(
                ref format,
                (nint)(delegate* unmanaged<nint, nint, nint, nint, uint, nint, void>)&InputCallback,
                GCHandle.ToIntPtr(self), 0, 0, 0, out var queue);
            if (created != AudioToolbox.NoErr)
            {
                self.Free();
                _core = null;
                _self = default;
                throw MapError(created);
            }

            _queue = queue;
            try
            {
                for (var i = 0; i < BufferCount; i++)
                {
                    Check(AudioToolbox.AudioQueueAllocateBuffer(queue, BufferBytes, out var buffer));
                    Check(AudioToolbox.AudioQueueEnqueueBuffer(queue, buffer, 0, 0));
                }

                Check(AudioToolbox.AudioQueueStart(queue, 0));
            }
            catch
            {
                AudioToolbox.AudioQueueDispose(queue, immediate: true);
                self.Free();
                _queue = 0;
                _core = null;
                _self = default;
                throw;
            }

            _recording = true;
        }

        return Task.CompletedTask;
    }

    public Task<RecordedAudio> StopAsync()
    {
        if (!IsRecording)
            throw new InvalidOperationException("目前沒有在錄音。");

        return Task.Run(() => Finish(keep: true) ?? throw new InvalidOperationException("錄音已經停止。"));
    }

    public void Cancel() => Finish(keep: false);

    public void Dispose() => Cancel();

    private RecordedAudio? Finish(bool keep)
    {
        PcmRecorderCore? core;
        nint queue;
        GCHandle self;
        lock (_gate)
        {
            if (!_recording)
                return null;

            _recording = false;
            _stopping = true;
            // 欄位保留到 Dispose 之後：停止期間到達的最後幾個緩衝仍要寫進 core
            core = _core;
            queue = _queue;
            self = _self;
        }

        // Stop 與 Dispose 都用 immediate：返回後不會再有回呼
        AudioToolbox.AudioQueueStop(queue, immediate: true);
        AudioToolbox.AudioQueueDispose(queue, immediate: true);

        lock (_gate)
        {
            _queue = 0;
            _core = null;
            _self = default;
        }

        if (self.IsAllocated)
            self.Free();

        return keep && core is not null ? core.Complete() : null;
    }

    private static AudioRecorderException MapError(int status) => status == AudioToolbox.KAudioHardwareBadDeviceError
        ? new AudioRecorderException(NoDevice, NoDeviceMessage)
        : new AudioRecorderException(Unknown, $"無法開始錄音（系統錯誤 {status}）。");

    private static void Check(int status)
    {
        if (status != AudioToolbox.NoErr)
            throw MapError(status);
    }

    /// <summary>AudioQueueInputCallback：只複製 PCM、重新排入緩衝，不碰 UI、不丟例外。</summary>
    [UnmanagedCallersOnly]
    private static void InputCallback(nint userData, nint queue, nint buffer, nint startTime, uint packetCount, nint packetDescs)
    {
        try
        {
            (GCHandle.FromIntPtr(userData).Target as MacAudioQueueRecorder)?.OnBuffer(queue, buffer);
        }
        catch
        {
            // 原生回呼不可丟例外
        }
    }

    private void OnBuffer(nint queue, nint buffer)
    {
        var audio = (AudioToolbox.AudioQueueBuffer*)buffer;
        var core = _core;
        var hadData = audio->DataByteSize > 0 && core is not null;
        var result = hadData
            ? core!.Append(new ReadOnlySpan<byte>((void*)audio->Data, (int)audio->DataByteSize))
            : default;

        if (!_stopping)
            AudioToolbox.AudioQueueEnqueueBuffer(queue, buffer, 0, 0);

        try
        {
            if (hadData)
                LevelChanged?.Invoke(this, result.Level);
            if (result.MaxReachedNow)
                MaxDurationReached?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 事件處理器的例外不能回到原生層
        }
    }
}
