namespace Lumi.App;

/// <summary>
/// 開發用的開關（appsettings／環境變數，<c>Lumi:Developer:*</c>），不是給一般使用者的設定。
/// </summary>
public sealed class DeveloperOptions
{
    /// <summary>
    /// 工作台輸入區多一個「小精靈模式」切換，送出時以小精靈模式送（短回答、<c>show_details</c>）。
    /// M7 的小精靈殼上線前，這是唯一能驗收小精靈 prompt 的地方；M7 上線後移除。
    /// </summary>
    public bool ShowSpriteModeToggle { get; set; }
}
