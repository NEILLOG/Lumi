namespace Lumi.Core.Models;

/// <summary>
/// 視窗與啟動行為。<paramref name="WorkbenchTopmost"/>：工作台視窗永遠在最上層（標題列的圖釘），預設關閉；
/// <paramref name="SpriteTopmost"/>：小精靈永遠在最上層，預設開啟（它本來就是浮在桌面上的）；
/// <paramref name="CheckForUpdates"/>：啟動時檢查有沒有新版；
/// <paramref name="SkippedVersion"/>：使用者按了「略過此版本」的版本號（空字串＝沒有略過），更新的版本出現時才會再提醒。
/// 「開機後自動啟動」不存在這裡：它的實際狀態在作業系統（登錄檔、LaunchAgent），以作業系統為準（<c>ILaunchAtLogin</c>）。
/// </summary>
public sealed record BehaviorSettings(
    bool WorkbenchTopmost = false,
    bool SpriteTopmost = true,
    bool CheckForUpdates = true,
    string SkippedVersion = "")
{
    public static readonly BehaviorSettings Default = new();

    public BehaviorSettings Normalize() => this with { SkippedVersion = (SkippedVersion ?? "").Trim() };
}
