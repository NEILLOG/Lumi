using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Lumi.Platform.Interop.Mac.ObjC;

namespace Lumi.Platform.Interop.Mac;

/// <summary>麥克風權限：<c>+[AVCaptureDevice authorizationStatusForMediaType:]</c>。</summary>
[SupportedOSPlatform("macos")]
internal static class AVFoundation
{
    private const string Lib = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    // AVAuthorizationStatus（NSInteger）
    public const nint NotDetermined = 0;
    public const nint Restricted = 1;
    public const nint Denied = 2;
    public const nint Authorized = 3;

    /// <summary>無法判斷（框架或類別不存在）。</summary>
    public const nint Unknown = -1;

    private static readonly Lazy<IntPtr> Handle = new(() => NativeLibrary.Load(Lib));

    /// <summary>
    /// 麥克風的授權狀態（<c>AVMediaTypeAudio</c> 是框架匯出的 NSString 變數，用 <see cref="CoreFoundation.ReadConstant(IntPtr, string)"/> 讀出）。
    /// 框架載入失敗時回 <see cref="Unknown"/>，由呼叫端決定要不要擋。
    /// </summary>
    internal static nint AudioAuthorizationStatus()
    {
        try
        {
            var cls = objc_getClass("AVCaptureDevice");
            var mediaType = CoreFoundation.ReadConstant(Handle.Value, "AVMediaTypeAudio");
            if (cls == 0 || mediaType == 0)
                return Unknown;

            return SendPtrArg(cls, Sel("authorizationStatusForMediaType:"), mediaType);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Unknown;
        }
    }
}
