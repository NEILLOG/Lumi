using System.ComponentModel;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;
using Lumi.Core.Web;

namespace Lumi.Tools.Web;

public sealed record NetCheckArgs(
    [property: Description("要檢查的網址，例如 https://intranet.example.com/；只給主機名稱時會當成 https://主機/")] string Url);

/// <summary>網路診斷：走哪個 Proxy、DNS、TCP、HTTP 回應碼、TLS 憑證。不讀取網頁內容（不包 web_content）。</summary>
public sealed class NetCheckTool(INetworkDiagnostics diagnostics) : ToolBase<NetCheckArgs>
{
    /// <summary>沒有 scheme 的主機名稱（例如 intranet.example.com:8080/path）。</summary>
    private static readonly Regex BareHost = new(@"^[A-Za-z0-9.-]+(:\d+)?(/.*)?$", RegexOptions.CultureInvariant);

    public override ToolDescriptor Descriptor { get; } = new(
        "net_check", "網路診斷",
        "診斷連到某個網址的網路狀況：會走哪個 Proxy（或直接連線）、DNS 查詢、TCP 連線、HTTP 回應碼與 TLS 憑證（簽發者與到期日）。" +
        "使用者問為什麼連不到某個網站，或其他網路工具失敗時使用。不會讀取網頁內容。",
        ToolPermission.Network, ToolModes.Both, TimeSpan.FromSeconds(45),
        Group: ToolGroups.Web, Summary: "檢查連到某個網址的網路狀況：Proxy、DNS、連線、憑證。");

    protected override ToolPreview Describe(NetCheckArgs args, ToolContext context) =>
        WebToolSupport.Preview(Normalize(args.Url));

    protected override async Task<ToolResult> InvokeAsync(NetCheckArgs args, ToolContext context, CancellationToken ct)
    {
        if (!WebToolSupport.TryParseUrl(Normalize(args.Url), out var url, out var error))
            return ToolResult.Error(error, "網址不正確");

        var result = await diagnostics.CheckAsync(url, ct);
        var summary = result.StatusCode is int code ? $"HTTP {code}" : "連不上";
        return ToolResult.Ok(Render(result), summary);
    }

    /// <summary>去空白；沒有 :// 且像主機名稱的，前面補 https://。</summary>
    internal static string Normalize(string? raw)
    {
        var text = raw?.Trim() ?? "";
        return !text.Contains("://", StringComparison.Ordinal) && BareHost.IsMatch(text) ? "https://" + text : text;
    }

    private static string Render(NetworkCheckResult result)
    {
        var viaProxy = result.Proxy.Proxy is not null;
        var lines = new List<string>
        {
            $"目標：{result.Target.AbsoluteUri}",
            $"Proxy：{(viaProxy ? $"經過 {result.Proxy.Proxy!.AbsoluteUri}" : "直接連線")}（{ModeName(result.Proxy.Mode)}）",
        };
        if (result.Proxy.Problem is not null)
            lines.Add($"Proxy 問題：{result.Proxy.Problem}");

        var dns = result.DnsError is null
            ? (result.Addresses.Count > 0 ? string.Join("、", result.Addresses) : "（沒有位址）")
            : result.DnsError;
        if (viaProxy && result.DnsError is not null)
            dns += "（走 Proxy 時本機查不到主機名稱是常見的，不一定是問題）";
        lines.Add($"DNS：{dns}");

        lines.Add(result.ConnectError is null
            ? $"TCP：連到 {result.ConnectTarget} 成功（{result.ConnectMs} 毫秒）"
            : $"TCP：連到 {result.ConnectTarget} 失敗：{result.ConnectError}");

        lines.Add(result.StatusCode is int code
            ? $"HTTP：{code}（{result.HttpMs} 毫秒）"
            : $"HTTP：失敗：{result.HttpError}");

        if (result.Target.Scheme == "https" && result.Certificate is { } cert)
        {
            lines.Add($"TLS 憑證：{cert.Subject}；簽發者 {cert.Issuer}；有效到 {cert.NotAfter.ToLocalTime():yyyy-MM-dd}");
            lines.Add("說明：如果簽發者是公司的名稱，而不是公開的憑證機構，代表公司網路有做 TLS 檢查。");
        }
        return string.Join("\n", lines);
    }

    private static string ModeName(ProxyMode mode) => mode switch
    {
        ProxyMode.System => "系統設定",
        ProxyMode.Pac => "PAC",
        ProxyMode.Manual => "手動",
        _ => "不使用 Proxy",
    };
}
