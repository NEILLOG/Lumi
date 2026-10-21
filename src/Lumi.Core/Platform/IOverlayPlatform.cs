namespace Lumi.Core.Platform;

/// <summary>游標在視窗用戶區的座標（DIP）。可以在視窗外（負值或超出尺寸）。</summary>
public readonly record struct OverlayPoint(double X, double Y);

/// <summary>
/// 小精靈視窗需要、但 Avalonia 沒有跨平台 API 的兩件事：
/// 1. 讀取全域游標（即使視窗正在點擊穿透、收不到滑鼠事件）
/// 2. 讓整個視窗忽略滑鼠（點擊穿透到下方桌面）
/// 只吃原生視窗控制代碼，不認識 Avalonia（Core 不引用 Avalonia）；每個視窗各用一個實例。
/// </summary>
public interface IOverlayPlatform
{
    /// <summary>給診斷畫面看的名稱。</summary>
    string Name { get; }

    /// <summary>
    /// <see cref="Attach"/> 成功之後為 true。false＝退化：沒有游標輪詢與點擊穿透，
    /// 殼只能在游標進入視窗時偵測靠近。
    /// </summary>
    bool IsSupported { get; }

    bool ClickThroughActive { get; }

    /// <summary>視窗 Opened 之後呼叫。拿不到有效的控制代碼或原生呼叫失敗時，<see cref="IsSupported"/> 維持 false，不丟例外。</summary>
    void Attach(nint nativeWindowHandle);

    /// <param name="renderScaling">視窗的 <c>RenderScaling</c>（Windows 用來把像素換成 DIP）。</param>
    /// <param name="clientHeightDip">用戶區高度（macOS 的座標原點在左下，要用它翻轉）。</param>
    bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out OverlayPoint dip);

    void SetClickThrough(bool on);
}

/// <summary>不支援的平台，或原生初始化失敗時的退化實作。</summary>
public sealed class NullOverlay : IOverlayPlatform
{
    public string Name => "Generic（無原生支援）";

    public bool IsSupported => false;

    public bool ClickThroughActive => false;

    public void Attach(nint nativeWindowHandle)
    {
    }

    public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out OverlayPoint dip)
    {
        dip = default;
        return false;
    }

    public void SetClickThrough(bool on)
    {
    }
}
