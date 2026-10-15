using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.OrbPoc.Platform;

[SupportedOSPlatform("macos")]
public sealed class MacOverlay : IOverlayPlatform
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint { public double X, Y; }

    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nuint SendRetNUInt(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendNUInt(IntPtr receiver, IntPtr selector, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern CGPoint SendRetPoint(IntPtr receiver, IntPtr selector);

    // NSWindowCollectionBehavior
    const nuint CanJoinAllSpaces = 1 << 0;
    const nuint FullScreenAuxiliary = 1 << 8;

    readonly Window _window;
    IntPtr _nsWindow;
    bool _active;

    public MacOverlay(Window window) => _window = window;

    public string Name => "macOS（AppKit via objc_msgSend）";
    public bool SupportsCursorPolling => _nsWindow != IntPtr.Zero;
    public bool SupportsClickThrough => _nsWindow != IntPtr.Zero;
    public bool ClickThroughActive => _active;

    static IntPtr Sel(string name) => sel_registerName(name);

    public void Initialize()
    {
        var handle = _window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;

        // Avalonia 在 macOS 回傳的是 NSView；從 view 取得所屬的 NSWindow
        _nsWindow = handle.HandleDescriptor == "NSWindow" ? handle.Handle : SendPtr(handle.Handle, Sel("window"));
        if (_nsWindow == IntPtr.Zero) return;

        // 透明無框視窗不要系統陰影（否則會在光球外圍出現方形陰影）
        SendBool(_nsWindow, Sel("setHasShadow:"), false);

        // 跟著切換桌面（Spaces），並允許出現在全螢幕 App 之上
        nuint behavior = SendRetNUInt(_nsWindow, Sel("collectionBehavior"));
        SendNUInt(_nsWindow, Sel("setCollectionBehavior:"), behavior | CanJoinAllSpaces | FullScreenAuxiliary);
    }

    public bool TryGetCursorInWindow(out Point dip)
    {
        dip = default;
        if (_nsWindow == IntPtr.Zero) return false;
        // 視窗座標（points、左下為原點），無框視窗的 frame 即內容區
        var p = SendRetPoint(_nsWindow, Sel("mouseLocationOutsideOfEventStream"));
        dip = new Point(p.X, _window.ClientSize.Height - p.Y);
        return true;
    }

    public void SetClickThrough(bool on)
    {
        if (_nsWindow == IntPtr.Zero || on == _active) return;
        SendBool(_nsWindow, Sel("setIgnoresMouseEvents:"), on);
        _active = on;
    }
}
