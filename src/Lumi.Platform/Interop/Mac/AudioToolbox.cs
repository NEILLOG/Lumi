using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Platform.Interop.Mac;

/// <summary>AudioToolbox 的 AudioQueue（錄音）。只宣告用到的部分；回傳值都是 OSStatus（0 表示成功）。</summary>
[SupportedOSPlatform("macos")]
internal static partial class AudioToolbox
{
    private const string Lib = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    public const int NoErr = 0;

    /// <summary>kAudioFormatLinearPCM（'lpcm'）</summary>
    public const uint KAudioFormatLinearPCM = 0x6C70636D;

    /// <summary>kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked</summary>
    public const uint KAudioFormatFlagsPcm16 = (1 << 2) | (1 << 3);

    /// <summary>kAudioHardwareBadDeviceError（'!dev'）：沒有可用的輸入裝置。</summary>
    public const int KAudioHardwareBadDeviceError = 0x21646576;

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatId;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    /// <summary>AudioQueueBuffer。回呼裡只讀 <see cref="DataByteSize"/> 與 <see cref="Data"/>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioQueueBuffer
    {
        public uint CapacityBytes;
        public nint Data;
        public uint DataByteSize;
        public nint UserData;
        public uint PacketDescCapacity;
        public nint PacketDescs;
        public uint PacketDescCount;
    }

    [LibraryImport(Lib)]
    internal static partial int AudioQueueNewInput(
        ref AudioStreamBasicDescription format, nint callback, nint userData, nint callbackRunLoop, nint callbackRunLoopMode,
        uint flags, out nint queue);

    [LibraryImport(Lib)]
    internal static partial int AudioQueueAllocateBuffer(nint queue, uint byteSize, out nint buffer);

    [LibraryImport(Lib)]
    internal static partial int AudioQueueEnqueueBuffer(nint queue, nint buffer, uint packetDescCount, nint packetDescs);

    [LibraryImport(Lib)]
    internal static partial int AudioQueueStart(nint queue, nint startTime);

    [LibraryImport(Lib)]
    internal static partial int AudioQueueStop(nint queue, [MarshalAs(UnmanagedType.U1)] bool immediate);

    [LibraryImport(Lib)]
    internal static partial int AudioQueueDispose(nint queue, [MarshalAs(UnmanagedType.U1)] bool immediate);
}
