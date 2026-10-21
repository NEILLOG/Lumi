namespace Lumi.Platform;

public sealed class OverlayOptions
{
    /// <summary>
    /// Windows：點擊穿透需要 <c>WS_EX_LAYERED</c> 加 <c>WS_EX_TRANSPARENT</c>。預設照 POC 驗證過的做法加 LAYERED；
    /// 萬一某台機器上透明渲染消失，改成 false（<c>Lumi:Developer:OverlayLayered</c>）。
    /// </summary>
    public bool Layered { get; set; } = true;
}
