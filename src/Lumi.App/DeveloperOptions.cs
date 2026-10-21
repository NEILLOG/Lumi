namespace Lumi.App;

/// <summary>
/// 開發用的開關（appsettings／環境變數，<c>Lumi:Developer:*</c>），不是給一般使用者的設定。
/// </summary>
public sealed class DeveloperOptions
{
    /// <summary>小精靈視窗的診斷 HUD（FPS、CPU、游標、點擊穿透狀態），F1 切換；補量 M0 還沒量的待機與休眠耗能。<c>Lumi:Developer:ShowSpriteHud</c></summary>
    public bool ShowSpriteHud { get; set; }
}
