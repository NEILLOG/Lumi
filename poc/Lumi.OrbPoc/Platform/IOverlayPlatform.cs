using Avalonia;
using Avalonia.Controls;

namespace Lumi.OrbPoc.Platform;

/// <summary>
/// 小精靈視窗需要、但 Avalonia 沒有跨平台 API 的兩件事：
/// 1. 讀取全域游標（即使視窗正在點擊穿透、收不到滑鼠事件）
/// 2. 讓整個視窗忽略滑鼠（點擊穿透到下方桌面）
/// </summary>
public interface IOverlayPlatform
{
    string Name { get; }
    bool SupportsCursorPolling { get; }
    bool SupportsClickThrough { get; }
    bool ClickThroughActive { get; }

    /// <summary>視窗 Opened 之後呼叫。</summary>
    void Initialize();

    /// <summary>游標在視窗用戶區的座標（DIP），可在視窗外（負值或超出尺寸）。</summary>
    bool TryGetCursorInWindow(out Point dip);

    void SetClickThrough(bool on);
}

public static class OverlayPlatform
{
    public static IOverlayPlatform Create(Window window)
    {
        if (OperatingSystem.IsWindows()) return new WindowsOverlay(window, useLayered: !App.HasArg("--no-layered"));
        if (OperatingSystem.IsMacOS()) return new MacOverlay(window);
        return new NullOverlay();
    }
}

public sealed class NullOverlay : IOverlayPlatform
{
    public string Name => "Generic（無原生支援）";
    public bool SupportsCursorPolling => false;
    public bool SupportsClickThrough => false;
    public bool ClickThroughActive => false;
    public void Initialize() { }
    public bool TryGetCursorInWindow(out Point dip) { dip = default; return false; }
    public void SetClickThrough(bool on) { }
}
