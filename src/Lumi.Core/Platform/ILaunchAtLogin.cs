namespace Lumi.Core.Platform;

/// <summary>
/// 登入系統後自動啟動 Lumi。狀態存在作業系統（Windows 的登錄檔 Run、macOS 的 LaunchAgent），不存進 settings.json：
/// 使用者在系統設定裡關掉它，Lumi 的開關也要跟著是關的。
/// </summary>
public interface ILaunchAtLogin
{
    /// <summary>這個執行方式能不能設定自動啟動。從 <c>dotnet run</c> 或不在 .app 套件裡的 macOS 執行檔啟動時是 false。</summary>
    bool IsSupported { get; }

    /// <summary>目前是否已設定自動啟動。</summary>
    bool IsEnabled();

    /// <summary>設定或取消。開啟時寫入「目前這個程式」的位置（程式被搬走之後重新開啟一次就會更新）。失敗丟 <see cref="IOException"/> 或 <see cref="UnauthorizedAccessException"/>。</summary>
    void SetEnabled(bool enabled);
}
