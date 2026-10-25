namespace Lumi.Browser;

/// <param name="Proxy">手動 Proxy（Playwright 的 Proxy 選項）；其他模式是 null。</param>
public sealed record BrowserProxySpec(string Server, string? Bypass, string? Username, string? Password);

/// <param name="Warning">不影響啟動、但可能讓連線失敗的提醒（例如 PAC 搭配帳號密碼）；沒有時 null。</param>
public sealed record BrowserLaunchSpec(
    string ExecutablePath, string UserDataDir, bool Headless, IReadOnlyList<string> Args, BrowserProxySpec? Proxy, string? Warning);

public static class BrowserLaunchPlanner
{
    /// <param name="forceVisible">true＝不論設定都顯示視窗（等使用者操作時用，D9）。</param>
    /// <param name="proxyPassword">網路設定的驗證是「帳號與密碼」時的 Proxy 密碼（可為 null）。</param>
    /// <exception cref="Lumi.Core.Browser.BrowserException">PAC 是 file:// 時丟 <c>ProxyNotSupported</c>。</exception>
    public static BrowserLaunchSpec Plan(
        Lumi.Core.Browser.InstalledBrowser browser, Lumi.Core.Browser.BrowserSettings browserSettings,
        Lumi.Core.Web.NetworkSettings network, string? proxyPassword, string profileRoot, bool forceVisible)
        => throw new NotImplementedException();
}
