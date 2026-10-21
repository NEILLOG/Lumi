using System.Runtime.Versioning;
using Lumi.Core.Platform;
using Lumi.Platform.Interop.Windows;

namespace Lumi.Platform.Overlay;

[SupportedOSPlatform("windows")]
public sealed class WindowsOverlay(bool useLayered) : IOverlayPlatform
{
    private nint _hwnd;
    private bool _active;

    public string Name => $"Windows（Win32{(useLayered ? " + WS_EX_LAYERED" : "，未加 LAYERED")}）";

    public bool IsSupported => _hwnd != 0;

    public bool ClickThroughActive => _active;

    public void Attach(nint nativeWindowHandle)
    {
        if (nativeWindowHandle == 0)
            return;

        // 頂層視窗要點擊穿透，通常需要 WS_EX_LAYERED + WS_EX_TRANSPARENT（M0 的 Windows 驗證通過）
        if (useLayered)
        {
            var style = User32.GetWindowLongPtr(nativeWindowHandle, User32.GwlExStyle);
            User32.SetWindowLongPtr(nativeWindowHandle, User32.GwlExStyle, (nint)((long)style | User32.WsExLayered));
            User32.SetLayeredWindowAttributes(nativeWindowHandle, 0, 255, User32.LwaAlpha);
        }
        _hwnd = nativeWindowHandle;
    }

    public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out OverlayPoint dip)
    {
        dip = default;
        if (_hwnd == 0 || renderScaling <= 0 || !User32.GetCursorPos(out var point) || !User32.ScreenToClient(_hwnd, ref point))
            return false;

        dip = new OverlayPoint(point.X / renderScaling, point.Y / renderScaling);
        return true;
    }

    public void SetClickThrough(bool on)
    {
        if (_hwnd == 0 || on == _active)
            return;

        var style = (long)User32.GetWindowLongPtr(_hwnd, User32.GwlExStyle);
        style = on ? style | User32.WsExTransparent : style & ~User32.WsExTransparent;
        User32.SetWindowLongPtr(_hwnd, User32.GwlExStyle, (nint)style);
        _active = on;
    }
}
