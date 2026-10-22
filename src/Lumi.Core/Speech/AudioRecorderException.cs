namespace Lumi.Core.Speech;

public enum AudioRecorderFailureKind
{
    PermissionDenied,
    NoDevice,
    DeviceBusy,
    Unsupported,
    Unknown,
}

/// <summary>錄音失敗。<see cref="Exception.Message"/> 是可以直接顯示給使用者的繁體中文，不含系統內部資訊。</summary>
public sealed class AudioRecorderException(AudioRecorderFailureKind kind, string message) : Exception(message)
{
    public AudioRecorderFailureKind Kind { get; } = kind;
}
