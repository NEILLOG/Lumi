using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Platform.Interop.Mac;

[SupportedOSPlatform("macos")]
internal static partial class CoreFoundation
{
    private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint KCFStringEncodingUTF8 = 0x08000100;

    [LibraryImport(Lib)]
    internal static partial void CFRelease(IntPtr cf);

    [LibraryImport(Lib)]
    private static partial SafeCFHandle CFStringCreateWithBytes(
        IntPtr allocator, ReadOnlySpan<byte> bytes, nint numBytes, uint encoding,
        [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);

    [LibraryImport(Lib)]
    private static partial SafeCFHandle CFDataCreate(IntPtr allocator, ReadOnlySpan<byte> bytes, nint length);

    [LibraryImport(Lib)]
    internal static partial nint CFDataGetLength(SafeCFHandle data);

    [LibraryImport(Lib)]
    internal static partial IntPtr CFDataGetBytePtr(SafeCFHandle data);

    [LibraryImport(Lib)]
    private static partial SafeCFHandle CFDictionaryCreateMutable(
        IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);

    [LibraryImport(Lib)]
    internal static partial void CFDictionarySetValue(SafeCFHandle dictionary, IntPtr key, IntPtr value);

    // framework 匯出的符號：資料在 dylib 裡，要用 GetExport 取位址，不能自己寫字串
    private static readonly Lazy<IntPtr> Handle = new(() => NativeLibrary.Load(Lib));

    /// <summary>取得匯出符號本身的位址（例如結構 <c>kCFTypeDictionaryKeyCallBacks</c>）。</summary>
    internal static IntPtr SymbolAddress(string symbol) => NativeLibrary.GetExport(Handle.Value, symbol);

    /// <summary>讀出匯出的指標變數（例如 <c>kCFBooleanTrue</c>、<c>kSecClass</c> 這類 CFTypeRef 常數）。</summary>
    internal static IntPtr ReadConstant(IntPtr libraryHandle, string symbol) =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(libraryHandle, symbol));

    internal static IntPtr ReadConstant(string symbol) => ReadConstant(Handle.Value, symbol);

    private static readonly Lazy<IntPtr> KeyCallbacks = new(() => SymbolAddress("kCFTypeDictionaryKeyCallBacks"));
    private static readonly Lazy<IntPtr> ValueCallbacks = new(() => SymbolAddress("kCFTypeDictionaryValueCallBacks"));
    private static readonly Lazy<IntPtr> True = new(() => ReadConstant("kCFBooleanTrue"));

    internal static IntPtr BooleanTrue => True.Value;

    internal static SafeCFHandle CreateString(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, KCFStringEncodingUTF8, false);
    }

    internal static SafeCFHandle CreateData(byte[] bytes) =>
        CFDataCreate(IntPtr.Zero, bytes, bytes.Length);

    /// <summary>建立會 retain 鍵與值的可變字典，所以放進去的 CF 物件之後可以先 Dispose。</summary>
    internal static SafeCFHandle CreateDictionary() =>
        CFDictionaryCreateMutable(IntPtr.Zero, 0, KeyCallbacks.Value, ValueCallbacks.Value);

    internal static void Set(SafeCFHandle dictionary, IntPtr key, SafeCFHandle value) =>
        CFDictionarySetValue(dictionary, key, value.DangerousGetHandle());

    internal static void Set(SafeCFHandle dictionary, IntPtr key, IntPtr borrowedValue) =>
        CFDictionarySetValue(dictionary, key, borrowedValue);
}
