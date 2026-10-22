using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Platform.Interop.Mac;

/// <summary>Objective-C 執行階段：用 <c>objc_msgSend</c> 呼叫 AppKit。每種參數／回傳型別各宣告一個簽章。</summary>
[SupportedOSPlatform("macos")]
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    internal struct CGPoint
    {
        public double X;
        public double Y;
    }

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint sel_registerName(string name);

    internal static nint Sel(string name) => sel_registerName(name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint objc_getClass(string name);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendPtrArg(nint receiver, nint selector, nint argument);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nint SendPtr(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void SendBool(nint receiver, nint selector, [MarshalAs(UnmanagedType.U1)] bool value);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SendRetBool(nint receiver, nint selector, nint argument);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial nuint SendRetNUInt(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial void SendNUInt(nint receiver, nint selector, nuint value);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    internal static partial CGPoint SendRetPoint(nint receiver, nint selector);
}
