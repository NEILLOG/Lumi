using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Lumi.Platform.Interop.Windows;

[SupportedOSPlatform("windows")]
internal static partial class User32
{
    private const string Lib = "user32.dll";

    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x00000020;
    public const long WsExLayered = 0x00080000;
    public const uint LwaAlpha = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport(Lib, EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hWnd, int index);

    [LibraryImport(Lib, EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint hWnd, int index, nint value);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hWnd, uint key, byte alpha, uint flags);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out Point point);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(nint hWnd, ref Point point);
}
