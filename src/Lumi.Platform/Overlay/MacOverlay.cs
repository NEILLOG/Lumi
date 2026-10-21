using System.Runtime.Versioning;
using Lumi.Core.Platform;
using static Lumi.Platform.Interop.Mac.ObjC;

namespace Lumi.Platform.Overlay;

[SupportedOSPlatform("macos")]
public sealed class MacOverlay : IOverlayPlatform
{
    // NSWindowCollectionBehavior
    private const nuint CanJoinAllSpaces = 1 << 0;
    private const nuint FullScreenAuxiliary = 1 << 8;

    private nint _nsWindow;
    private bool _active;

    public string Name => "macOS（AppKit via objc_msgSend）";

    public bool IsSupported => _nsWindow != 0;

    public bool ClickThroughActive => _active;

    public void Attach(nint nativeWindowHandle)
    {
        if (nativeWindowHandle == 0)
            return;

        // Avalonia 在 macOS 給的可能是 NSWindow，也可能是 NSView：能收 setIgnoresMouseEvents: 的就是 NSWindow，
        // 否則從 view 取得所屬的 NSWindow（對 NSWindow 呼叫 -window 會丟 unrecognized selector）
        var window = SendRetBool(nativeWindowHandle, Sel("respondsToSelector:"), Sel("setIgnoresMouseEvents:"))
            ? nativeWindowHandle
            : SendPtr(nativeWindowHandle, Sel("window"));
        if (window == 0)
            return;

        // 透明無框視窗不要系統陰影（否則會在光球外圍出現方形陰影）
        SendBool(window, Sel("setHasShadow:"), false);

        // 跟著切換桌面（Spaces），並允許出現在全螢幕 App 之上
        var behavior = SendRetNUInt(window, Sel("collectionBehavior"));
        SendNUInt(window, Sel("setCollectionBehavior:"), behavior | CanJoinAllSpaces | FullScreenAuxiliary);
        _nsWindow = window;
    }

    public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out OverlayPoint dip)
    {
        dip = default;
        if (_nsWindow == 0)
            return false;

        // 視窗座標（points、左下為原點），無框視窗的 frame 即內容區
        var point = SendRetPoint(_nsWindow, Sel("mouseLocationOutsideOfEventStream"));
        dip = new OverlayPoint(point.X, clientHeightDip - point.Y);
        return true;
    }

    public void SetClickThrough(bool on)
    {
        if (_nsWindow == 0 || on == _active)
            return;

        SendBool(_nsWindow, Sel("setIgnoresMouseEvents:"), on);
        _active = on;
    }
}
