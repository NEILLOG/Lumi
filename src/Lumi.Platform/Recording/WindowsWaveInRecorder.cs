using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumi.Core.Speech;
using Lumi.Platform.Interop.Windows;
using Microsoft.Win32;
using static Lumi.Core.Speech.AudioRecorderFailureKind;

namespace Lumi.Platform.Recording;

/// <summary>
/// Windows 錄音：winmm <c>waveIn</c>（CALLBACK_FUNCTION）。16 kHz／單聲道／16-bit，系統直接轉成這個格式。
/// 四個 50 ms 的緩衝輪流重送；回呼只把 PCM 複製給 <see cref="PcmRecorderCore"/>。
/// 停止順序：waveInStop → waveInReset → 等所有緩衝歸還 → waveInUnprepareHeader → waveInClose → 釋放原生記憶體。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsWaveInRecorder : IAudioRecorder, IDisposable
{
    private const int BufferCount = 4;
    private const int BufferBytes = WavFormat.BytesPerSecond / 20; // 50 ms，讓音量約每 50 ms 更新一次
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private const string NoDeviceMessage =
        "找不到可用的麥克風。請確認麥克風已連接，並在「設定 › 隱私權與安全性 › 麥克風」允許桌面應用程式存取。";

    private const string PermissionDeniedMessage =
        "麥克風權限已關閉。請到「設定 › 隱私權與安全性 › 麥克風」，開啟「允許桌面應用程式存取麥克風」。";

    private const string PrivacyConsentRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private readonly object _gate = new();
    private Capture? _capture;

    public event EventHandler<float>? LevelChanged;

    public event EventHandler? MaxDurationReached;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
                return _capture is { Stopping: false };
        }
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_capture is not null)
                throw new InvalidOperationException("已經在錄音中。");

            // 隱私權設定關閉時 waveIn 不一定報錯，先看登錄檔的同意狀態，給出明確的指引
            if (MicrophoneDenied(PrivacyConsentRoot) || MicrophoneDenied(PrivacyConsentRoot + @"\NonPackaged"))
                throw new AudioRecorderException(PermissionDenied, PermissionDeniedMessage);

            if (WinMm.waveInGetNumDevs() == 0)
                throw new AudioRecorderException(NoDevice, NoDeviceMessage);

            var capture = new Capture(this);
            _capture = capture;
            try
            {
                capture.Open();
            }
            catch
            {
                capture.Shutdown();
                _capture = null;
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public Task<RecordedAudio> StopAsync()
    {
        if (!IsRecording)
            throw new InvalidOperationException("目前沒有在錄音。");

        // 等緩衝歸還會阻塞，放到背景執行緒，不卡住 UI
        return Task.Run(() => Finish(keep: true) ?? throw new InvalidOperationException("錄音已經停止。"));
    }

    public void Cancel() => Finish(keep: false);

    public void Dispose() => Cancel();

    private RecordedAudio? Finish(bool keep)
    {
        Capture capture;
        lock (_gate)
        {
            if (_capture is not { Stopping: false } active)
                return null;

            active.BeginStop();
            capture = active;
        }

        // 即使釋放失敗也把狀態清掉，讓之後可以重新開始（裝置還被占用時 waveInOpen 會回 DeviceBusy）
        capture.Shutdown();
        lock (_gate)
        {
            if (ReferenceEquals(_capture, capture))
                _capture = null;
        }

        return keep ? capture.Core.Complete() : null;
    }

    private static AudioRecorderException MapError(uint rc) => rc switch
    {
        WinMm.MmsyserrAllocated => new(DeviceBusy, "麥克風正被其他程式使用，請關閉那個程式後再試。"),
        WinMm.MmsyserrBadDeviceId or WinMm.MmsyserrNoDriver => new(NoDevice, NoDeviceMessage),
        WinMm.WaverrBadFormat => new(Unsupported, "這個麥克風不支援 16 kHz、單聲道、16-bit 的錄音格式。"),
        _ => new(Unknown, $"無法開始錄音（錯誤代碼 {rc}）。"),
    };

    private static void Check(uint rc)
    {
        if (rc != WinMm.MmsyserrNoError)
            throw MapError(rc);
    }

    /// <summary>登錄檔的麥克風同意狀態為 "Deny"。讀不到（沒有這個機碼）時視為允許。</summary>
    private static bool MicrophoneDenied(string subKey)
    {
        try
        {
            return string.Equals(Registry.GetValue($@"HKEY_CURRENT_USER\{subKey}", "Value", null) as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>一次錄音的原生狀態：waveIn 控制代碼、緩衝、GCHandle 與回呼。停止後才釋放。</summary>
    private sealed class Capture
    {
        private readonly WindowsWaveInRecorder _owner;
        private readonly nint[] _headers = new nint[BufferCount];
        private readonly nint[] _buffers = new nint[BufferCount];
        private readonly ManualResetEventSlim _drained = new(false);
        private GCHandle _self;
        private nint _hwi;
        private int _prepared;
        private int _inFlight;   // 目前還在驅動程式手上的緩衝數
        private volatile bool _stopping;

        public Capture(WindowsWaveInRecorder owner)
        {
            _owner = owner;
        }

        public PcmRecorderCore Core { get; } = new();

        public bool Stopping => _stopping;

        public void BeginStop() => _stopping = true;

        public void Open()
        {
            _self = GCHandle.Alloc(this);

            var format = new WinMm.WaveFormatEx
            {
                FormatTag = WinMm.WaveFormatPcm,
                Channels = WavFormat.Channels,
                SamplesPerSec = WavFormat.SampleRate,
                AvgBytesPerSec = WavFormat.BytesPerSecond,
                BlockAlign = WavFormat.BlockAlign,
                BitsPerSample = WavFormat.BitsPerSample,
            };

            var opened = WinMm.waveInOpen(out var hwi, WinMm.WaveMapper, ref format,
                (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, void>)&WaveInProc,
                GCHandle.ToIntPtr(_self), WinMm.CallbackFunction);
            Check(opened);   // 失敗時 hwi 不會被寫入，所以只在成功後才保存
            _hwi = hwi;

            var headerSize = (uint)sizeof(WinMm.WaveHdr);
            for (var i = 0; i < BufferCount; i++)
            {
                _buffers[i] = Marshal.AllocHGlobal(BufferBytes);
                _headers[i] = Marshal.AllocHGlobal(sizeof(WinMm.WaveHdr));
                *(WinMm.WaveHdr*)_headers[i] = new WinMm.WaveHdr { Data = _buffers[i], BufferLength = BufferBytes };

                Check(WinMm.waveInPrepareHeader(_hwi, _headers[i], headerSize));
                _prepared++;
                Check(WinMm.waveInAddBuffer(_hwi, _headers[i], headerSize));
                Interlocked.Increment(ref _inFlight);
            }

            Check(WinMm.waveInStart(_hwi));
        }

        /// <summary>
        /// 停止收音並釋放原生資源。回傳 false 表示緩衝沒有全部歸還：此時不釋放記憶體與 GCHandle
        /// （驅動程式可能還會寫入），寧可漏掉也不要存取已釋放的記憶體。
        /// </summary>
        public bool Shutdown()
        {
            _stopping = true;
            if (_hwi != 0)
            {
                WinMm.waveInStop(_hwi);
                WinMm.waveInReset(_hwi);
            }

            var returned = Volatile.Read(ref _inFlight) == 0 || _drained.Wait(DrainTimeout);
            if (!returned)
                return false;

            var headerSize = (uint)sizeof(WinMm.WaveHdr);
            for (var i = 0; i < _prepared; i++)
                WinMm.waveInUnprepareHeader(_hwi, _headers[i], headerSize);
            _prepared = 0;

            if (_hwi != 0)
            {
                WinMm.waveInClose(_hwi);
                _hwi = 0;
            }

            for (var i = 0; i < BufferCount; i++)
            {
                if (_headers[i] != 0)
                    Marshal.FreeHGlobal(_headers[i]);
                if (_buffers[i] != 0)
                    Marshal.FreeHGlobal(_buffers[i]);
                _headers[i] = 0;
                _buffers[i] = 0;
            }

            if (_self.IsAllocated)
                _self.Free();
            return true;
        }

        /// <summary>原生回呼執行緒（WIM_DATA）：複製 PCM、重新排入緩衝，最後才發出事件。</summary>
        internal void OnData(nint headerPtr)
        {
            var hdr = (WinMm.WaveHdr*)headerPtr;
            var hadData = hdr->BytesRecorded > 0;
            var result = hadData
                ? Core.Append(new ReadOnlySpan<byte>((void*)hdr->Data, checked((int)hdr->BytesRecorded)))
                : default;

            var requeued = false;
            if (!_stopping)
            {
                hdr->BytesRecorded = 0;
                requeued = WinMm.waveInAddBuffer(_hwi, headerPtr, (uint)sizeof(WinMm.WaveHdr)) == WinMm.MmsyserrNoError;
            }

            if (!requeued && Interlocked.Decrement(ref _inFlight) == 0)
                _drained.Set();

            try
            {
                if (hadData)
                    _owner.LevelChanged?.Invoke(_owner, result.Level);
                if (result.MaxReachedNow)
                    _owner.MaxDurationReached?.Invoke(_owner, EventArgs.Empty);
            }
            catch
            {
                // 事件處理器的例外不能回到原生層（否則緩衝會丟失）；畫面端自己負責處理錯誤
            }
        }

        /// <summary>WAVEINPROC（stdcall）。回呼內不丟例外，也不碰 UI。</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static void WaveInProc(nint hwi, uint msg, nint instance, nint param1, nint param2)
        {
            if (msg != WinMm.WimData)
                return;

            try
            {
                (GCHandle.FromIntPtr(instance).Target as Capture)?.OnData(param1);
            }
            catch
            {
                // 原生回呼不可丟例外
            }
        }
    }
}
