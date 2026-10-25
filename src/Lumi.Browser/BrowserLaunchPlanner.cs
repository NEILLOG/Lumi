using Lumi.Core.Browser;
using Lumi.Core.Web;

namespace Lumi.Browser;

/// <param name="Proxy">手動 Proxy（Playwright 的 Proxy 選項）；其他模式是 null。</param>
public sealed record BrowserProxySpec(string Server, string? Bypass, string? Username, string? Password);

/// <param name="Warning">不影響啟動、但可能讓連線失敗的提醒（例如 PAC 搭配帳號密碼）；沒有時 null。</param>
public sealed record BrowserLaunchSpec(
    string ExecutablePath, string UserDataDir, bool Headless, IReadOnlyList<string> Args, BrowserProxySpec? Proxy, string? Warning);

/// <summary>把瀏覽器設定與網路設定（M17）轉成瀏覽器的啟動規格（D8）。純計算，不啟動瀏覽器。</summary>
public static class BrowserLaunchPlanner
{
    /// <param name="forceVisible">true＝不論設定都顯示視窗（等使用者操作時用，D9）。</param>
    /// <param name="proxyPassword">網路設定的驗證是「帳號與密碼」時的 Proxy 密碼（可為 null）。</param>
    /// <exception cref="BrowserException">PAC 不是 http／https 網址、或手動 Proxy 格式錯誤時丟 <c>ProxyNotSupported</c>。</exception>
    public static BrowserLaunchSpec Plan(
        InstalledBrowser browser, BrowserSettings browserSettings,
        NetworkSettings network, string? proxyPassword, string profileRoot, bool forceVisible)
    {
        var args = new List<string>();
        BrowserProxySpec? proxy = null;
        string? warning = null;

        switch (network.Mode)
        {
            case ProxyMode.None:
                args.Add("--no-proxy-server");
                break;
            case ProxyMode.Pac:
                if (!Uri.TryCreate(network.PacUrl, UriKind.Absolute, out var pac)
                    || pac.Scheme is not ("http" or "https"))
                    throw new BrowserException(
                        BrowserFailureKind.ProxyNotSupported,
                        "瀏覽器不支援 file:// 的 PAC 檔，請改用 http 或 https 開頭的 PAC 網址。");
                args.Add("--proxy-pac-url=" + network.PacUrl);
                break;
            case ProxyMode.Manual:
                if (!NetworkRules.TryParseManualProxy(network.ManualProxy, out var server))
                    throw new BrowserException(
                        BrowserFailureKind.ProxyNotSupported,
                        "Proxy 位址格式不正確，請到設定的「網路」頁檢查。");
                var useAccount = network.Auth == ProxyAuthMode.Account && !string.IsNullOrWhiteSpace(network.ProxyUser);
                proxy = new BrowserProxySpec(
                    server.AbsoluteUri.TrimEnd('/'),
                    network.BypassList.Count > 0 ? string.Join(",", network.BypassList.Select(ToChromiumBypassRule)) : null,
                    useAccount ? network.ProxyUser : null,
                    useAccount ? proxyPassword : null);
                break;
        }

        if (network.Auth == ProxyAuthMode.Account && network.Mode is ProxyMode.System or ProxyMode.Pac)
            warning = "瀏覽器使用系統或 PAC 的 Proxy 時，無法自動帶入帳號與密碼；如果 Proxy 要求登入，請到設定打開「顯示瀏覽器視窗」，在視窗裡輸入。";

        if (network.IntegratedAuthSites.Count > 0)
            args.Add("--auth-server-allowlist=" + string.Join(",", network.IntegratedAuthSites));

        return new BrowserLaunchSpec(
            browser.Path,
            Path.Combine(profileRoot, browser.Kind == BrowserKind.Edge ? "edge" : "chrome"),
            !(browserSettings.ShowWindow || forceVisible),
            args,
            proxy,
            warning);
    }

    /// <summary>
    /// Lumi 的例外清單可以寫 <c>10.*</c>、<c>192.168.1.*</c>（M17 的萬用字元語法），Chromium 的 <c>--proxy-bypass-list</c> 不認得；
    /// 這種「前面是數字、後面都是 *」的 IPv4 樣式轉成 CIDR（<c>10.0.0.0/8</c>、<c>192.168.1.0/24</c>）。其他寫法（<c>*.example.com</c>、主機名稱、完整 IP）原樣交給 Chromium。
    /// </summary>
    internal static string ToChromiumBypassRule(string pattern)
    {
        var segments = pattern.Split('.');
        if (segments.Length is < 2 or > 4)
            return pattern;

        var numeric = segments.TakeWhile(s => int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n <= 255).Count();
        if (numeric == 0 || numeric == segments.Length || segments.Skip(numeric).Any(s => s != "*"))
            return pattern;

        var address = string.Join('.', segments.Take(numeric).Concat(Enumerable.Repeat("0", 4 - numeric)));
        return $"{address}/{numeric * 8}";
    }
}
