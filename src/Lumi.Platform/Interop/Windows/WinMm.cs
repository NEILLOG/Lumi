using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Platform.Interop.Windows;

/// <summary>winmm 的 waveIn（錄音）。只宣告用到的部分。</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WinMm
{
    private const string Lib = "winmm.dll";

    // MMRESULT
    public const uint MmsyserrNoError = 0;
    public const uint MmsyserrBadDeviceId = 2;
    public const uint MmsyserrAllocated = 4;
    public const uint MmsyserrNoDriver = 6;
    public const uint WaverrBadFormat = 32;

    public const uint WaveMapper = uint.MaxValue;     // (UINT)-1：系統預設的錄音裝置
    public const uint CallbackFunction = 0x00030000;  // CALLBACK_FUNCTION
    public const uint WimData = 0x3C0;                // WIM_DATA
    public const uint WhdrDone = 0x1;                 // WHDR_DONE
    public const ushort WaveFormatPcm = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort CbSize;
    }

    /// <summary>WAVEHDR。欄位順序與 Win32 相同（x86／x64 的自然對齊）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHdr
    {
        public nint Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public nint User;
        public uint Flags;
        public uint Loops;
        public nint Next;
        public nint Reserved;
    }

    [LibraryImport(Lib)]
    internal static partial uint waveInGetNumDevs();

    [LibraryImport(Lib)]
    internal static partial uint waveInOpen(out nint hwi, uint deviceId, ref WaveFormatEx format, nint callback, nint instance, uint flags);

    [LibraryImport(Lib)]
    internal static partial uint waveInPrepareHeader(nint hwi, nint header, uint size);

    [LibraryImport(Lib)]
    internal static partial uint waveInUnprepareHeader(nint hwi, nint header, uint size);

    [LibraryImport(Lib)]
    internal static partial uint waveInAddBuffer(nint hwi, nint header, uint size);

    [LibraryImport(Lib)]
    internal static partial uint waveInStart(nint hwi);

    [LibraryImport(Lib)]
    internal static partial uint waveInStop(nint hwi);

    [LibraryImport(Lib)]
    internal static partial uint waveInReset(nint hwi);

    [LibraryImport(Lib)]
    internal static partial uint waveInClose(nint hwi);
}
