using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Lumi.Platform.Interop.Mac;

/// <summary>擁有一個 CoreFoundation 物件的參考，Dispose 時 <c>CFRelease</c>，避免漏掉。</summary>
[SupportedOSPlatform("macos")]
internal sealed class SafeCFHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    // P/Invoke 回傳 SafeHandle 時需要
    public SafeCFHandle() : base(ownsHandle: true) { }

    public SafeCFHandle(IntPtr owned) : base(ownsHandle: true) => SetHandle(owned);

    protected override bool ReleaseHandle()
    {
        CoreFoundation.CFRelease(handle);
        return true;
    }
}
