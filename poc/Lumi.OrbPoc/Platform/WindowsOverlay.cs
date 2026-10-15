using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.OrbPoc.Platform;

[SupportedOSPlatform("windows")]
public sealed class WindowsOverlay : IOverlayPlatform
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x00000020;
    const long WS_EX_LAYERED = 0x00080000;
    const uint LWA_ALPHA = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);

    readonly Window _window;
    readonly bool _useLayered;
    IntPtr _hwnd;
    bool _active;

    public WindowsOverlay(Window window, bool useLayered)
    {
        _window = window;
        _useLayered = useLayered;
    }

    public string Name => $"Windows（Win32{(_useLayered ? " + WS_EX_LAYERED" : "，未加 LAYERED")}）";
    public bool SupportsCursorPolling => _hwnd != IntPtr.Zero;
    public bool SupportsClickThrough => _hwnd != IntPtr.Zero;
    public bool ClickThroughActive => _active;

    public void Initialize()
    {
        _hwnd = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (_hwnd == IntPtr.Zero) return;

        // 頂層視窗要點擊穿透，通常需要 WS_EX_LAYERED + WS_EX_TRANSPARENT。
        // 驗證重點：加上 LAYERED 後 Avalonia 的透明渲染是否仍正常（若畫面消失，改用 --no-layered 再測）。
        if (_useLayered)
        {
            long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
            SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
        }
    }

    public bool TryGetCursorInWindow(out Point dip)
    {
        dip = default;
        if (_hwnd == IntPtr.Zero || !GetCursorPos(out var p) || !ScreenToClient(_hwnd, ref p)) return false;
        double s = _window.RenderScaling;
        dip = new Point(p.X / s, p.Y / s);
        return true;
    }

    public void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero || on == _active) return;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        _active = on;
    }
}
