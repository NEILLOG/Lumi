using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Lumi.Tools.Web;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;

namespace Lumi.Tools.Tests.Web;

public sealed class NetCheckToolTests
{
    private readonly FakeNetworkDiagnostics _diagnostics = new();
    private readonly NetCheckTool _tool;

    public NetCheckToolTests() => _tool = new NetCheckTool(_diagnostics);

    private static ToolContext Ctx() => Context(EmptyFileScope.Instance);

    private Task<ToolResult> Run(object args) => _tool.ExecuteAsync(Json(args), Ctx(), default);

    private ToolPreview Preview(object args) => _tool.Preview(Json(args), Ctx());

    [Fact]
    public void Descriptor_IsNetwork_BothModes_FortyFiveSeconds_WebGroup()
    {
        var d = _tool.Descriptor;

        Assert.Equal("net_check", d.Name);
        Assert.Equal("網路診斷", d.DisplayName);
        Assert.Equal(ToolPermission.Network, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.True(d.Modes.Allows(ChatMode.Sprite));
        Assert.Equal(TimeSpan.FromSeconds(45), d.Timeout);
        Assert.Equal(ToolGroups.Web, d.Group);
    }

    [Fact]
    public async Task HostNameOnly_GetsHttps()
    {
        await Run(new { url = "intranet.example.com/app" });

        Assert.Equal("https://intranet.example.com/app", Assert.Single(_diagnostics.Targets).AbsoluteUri);
    }

    [Fact]
    public async Task ExplicitHttp_IsKept()
    {
        await Run(new { url = "http://old.example.com/" });

        Assert.Equal("http://old.example.com/", Assert.Single(_diagnostics.Targets).AbsoluteUri);
    }

    [Fact]
    public async Task DirectConnection_ShowsEachStep()
    {
        var result = await Run(new { url = "https://example.com/" });

        Assert.False(result.IsError);
        Assert.Contains("直接連線（系統設定）", result.Content);
        Assert.Contains("DNS：203.0.113.5", result.Content);
        Assert.Contains("TCP：連到 203.0.113.5:443 成功（12 毫秒）", result.Content);
        Assert.Contains("HTTP：200（85 毫秒）", result.Content);
        Assert.Contains("TLS 憑證：CN=example.com；簽發者 CN=Public CA；有效到 2027-01-01", result.Content);
        Assert.Contains("代表公司網路有做 TLS 檢查", result.Content);
        Assert.Equal("HTTP 200", result.Summary);
    }

    [Fact]
    public async Task ViaProxy_ShowsTheProxyAndTheModeName()
    {
        _diagnostics.Respond = target => FakeNetworkDiagnostics.Healthy(target) with
        {
            Proxy = new ProxyDecision(ProxyMode.Pac, new Uri("http://proxy.corp.example:8080/"), null),
        };

        var result = await Run(new { url = "https://example.com/" });

        Assert.Contains("Proxy：經過 http://proxy.corp.example:8080/（PAC）", result.Content);
    }

    [Fact]
    public async Task ProxyProblem_IsShown_WhenThereIsOne()
    {
        _diagnostics.Respond = target => FakeNetworkDiagnostics.Healthy(target) with
        {
            Proxy = new ProxyDecision(ProxyMode.System, null, "PAC 檔下載失敗：逾時，目前直接連線"),
        };

        var result = await Run(new { url = "https://example.com/" });

        Assert.Contains("Proxy 問題：PAC 檔下載失敗：逾時，目前直接連線", result.Content);
    }

    [Fact]
    public async Task DnsFailure_WithProxy_AddsTheExplanation()
    {
        _diagnostics.Respond = target => FakeNetworkDiagnostics.Healthy(target) with
        {
            Proxy = new ProxyDecision(ProxyMode.Manual, new Uri("http://proxy.corp.example:8080/"), null),
            Addresses = [],
            DnsError = "找不到主機 example.com",
        };

        var result = await Run(new { url = "https://example.com/" });

        Assert.Contains("DNS：找不到主機 example.com（走 Proxy 時本機查不到主機名稱是常見的，不一定是問題）", result.Content);
    }

    [Fact]
    public async Task DnsFailure_WithoutProxy_HasNoExplanation()
    {
        _diagnostics.Respond = target => FakeNetworkDiagnostics.Healthy(target) with
        {
            Addresses = [],
            DnsError = "找不到主機 example.com",
        };

        var result = await Run(new { url = "https://example.com/" });

        Assert.Contains("DNS：找不到主機 example.com", result.Content);
        Assert.DoesNotContain("常見的", result.Content);
    }

    [Fact]
    public async Task TlsSection_OnlyAppears_ForHttpsWithACertificate()
    {
        _diagnostics.Respond = target => FakeNetworkDiagnostics.Healthy(target) with { Certificate = null };
        var noCertificate = await Run(new { url = "https://example.com/" });

        _diagnostics.Respond = target => FakeNetworkDiagnostics.Healthy(target);
        var plainHttp = await Run(new { url = "http://example.com/" });

        Assert.DoesNotContain("TLS 憑證", noCertificate.Content);
        Assert.DoesNotContain("TLS 憑證", plainHttp.Content);
    }

    [Fact]
    public async Task FailedConnectAndHttp_AreListed_AndNotAnError()
    {
        _diagnostics.Respond = target => FakeNetworkDiagnostics.Healthy(target) with
        {
            ConnectError = "連線被拒",
            ConnectMs = null,
            StatusCode = null,
            HttpMs = null,
            HttpError = "連不上",
            Certificate = null,
        };

        var result = await Run(new { url = "https://example.com/" });

        Assert.False(result.IsError);
        Assert.Contains("TCP：連到 203.0.113.5:443 失敗：連線被拒", result.Content);
        Assert.Contains("HTTP：失敗：連不上", result.Content);
        Assert.Equal("連不上", result.Summary);
    }

    [Fact]
    public async Task InvalidUrl_IsError_AndNothingIsChecked()
    {
        var result = await Run(new { url = "ftp://example.com/" });

        Assert.True(result.IsError);
        Assert.Empty(_diagnostics.Targets);
    }

    [Fact]
    public void Preview_UsesTheNormalizedUrl()
    {
        var preview = Preview(new { url = "intranet.example.com" });

        Assert.NotNull(preview.Url);
        Assert.Equal("https://intranet.example.com/", preview.Url!.AbsoluteUri);
    }

    [Fact]
    public void Preview_WithBadUrl_IsNone()
    {
        var preview = Preview(new { url = "ftp://example.com/" });

        Assert.Null(preview.Url);
    }
}
