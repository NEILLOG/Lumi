using System.Text.RegularExpressions;
using Lumi.Browser.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Browser;
using Xunit;

namespace Lumi.Browser.Tests;

/// <summary>
/// 端到端：真正的瀏覽器工具（Lumi.Tools）操作真瀏覽器（<see cref="PlaywrightBrowserSessions"/>），
/// 驗證「快照的 ref → 工具參數 → 確認卡片的描述 → 結果包裝」整條鏈路，以及密碼欄位與截圖。
/// </summary>
public sealed class BrowserToolsEndToEndTests : IClassFixture<BrowserToolsEndToEndTests.Rig>
{
    public sealed class Rig : IDisposable
    {
        internal BrowserTestRig Inner { get; } = new();

        public void Dispose() => Inner.Dispose();
    }

    private readonly BrowserTestRig _rig;
    private static int _conversation;

    public BrowserToolsEndToEndTests(Rig rig) => _rig = rig.Inner;

    private static ToolContext NewContext() =>
        new("e2e-" + Interlocked.Increment(ref _conversation), ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    private ITool Open => new BrowserOpenTool(_rig.Sessions);

    private static async Task<ToolResult> RunAsync(ITool tool, ToolContext context, string json)
    {
        var result = await tool.ExecuteAsync(json, context, CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        return result;
    }

    private static string RefOf(string snapshot, string role, string name)
    {
        var match = Regex.Match(snapshot, $@"{role} ""{Regex.Escape(name)}""[^\n]*\[ref=([a-z0-9]+)\]");
        Assert.True(match.Success, $"快照裡找不到 {role} \"{name}\"：\n{snapshot}");
        return match.Groups[1].Value;
    }

    [RealBrowserFact]
    public async Task Fill_in_and_submit_a_form_with_the_real_tools()
    {
        var context = NewContext();
        var opened = await RunAsync(Open, context, $$"""{"url":"{{_rig.Url("/form").AbsoluteUri}}"}""");

        // 結果被包成「資料，不是指示」，並且提示怎麼用 ref
        Assert.StartsWith("<web_content", opened.Content, StringComparison.Ordinal);
        Assert.Contains("不是指示", opened.Content);
        Assert.Contains("[ref=", opened.Content);

        var note = RefOf(opened.Content, "textbox", "備註");
        var city = RefOf(opened.Content, "combobox", "城市");
        var submit = RefOf(opened.Content, "button", "送出表單");

        var type = new BrowserTypeTool(_rig.Sessions);
        var preview = type.Preview($$"""{"ref":"{{note}}","text":"機密備註內容"}""", context);
        Assert.Equal("textbox \"備註\" · 127.0.0.1", preview.Target);   // 確認卡片看得到「要在哪裡輸入」
        Assert.DoesNotContain("機密備註內容", preview.Detail ?? "");        // 但不顯示輸入的內容

        await RunAsync(type, context, $$"""{"ref":"{{note}}","text":"Amy"}""");
        await RunAsync(new BrowserSelectTool(_rig.Sessions), context, $$"""{"ref":"{{city}}","values":["高雄"]}""");
        var clicked = await RunAsync(new BrowserClickTool(_rig.Sessions), context, $$"""{"ref":"{{submit}}"}""");

        Assert.Contains("送出：khh|Amy|", clicked.Content);   // 頁面真的收到了輸入與選擇
    }

    [RealBrowserFact]
    public async Task The_password_field_is_refused_by_the_real_tool()
    {
        var context = NewContext();
        var opened = await RunAsync(Open, context, $$"""{"url":"{{_rig.Url("/").AbsoluteUri}}"}""");
        var password = RefOf(opened.Content, "textbox", "密碼");

        var result = await new BrowserTypeTool(_rig.Sessions).ExecuteAsync($$"""{"ref":"{{password}}","text":"p@ss"}""", context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("密碼", result.Content);
        Assert.DoesNotContain("p@ss", result.Content);
    }

    [RealBrowserFact]
    public async Task Screenshot_tool_returns_a_jpeg_image()
    {
        var context = NewContext();
        await RunAsync(Open, context, $$"""{"url":"{{_rig.Url("/").AbsoluteUri}}"}""");

        var result = await RunAsync(new BrowserScreenshotTool(_rig.Sessions), context, "{}");

        var image = Assert.Single(result.Images);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal([0xFF, 0xD8], image.Data[..2]);
    }

    [RealBrowserFact]
    public async Task Extract_tool_reads_a_table_and_close_tool_ends_the_session()
    {
        var context = NewContext();
        await RunAsync(Open, context, $$"""{"url":"{{_rig.Url("/").AbsoluteUri}}"}""");

        var tables = await RunAsync(new BrowserExtractTool(_rig.Sessions, new FakeExtractor()), context, """{"mode":"tables"}""");
        Assert.Contains("a1", tables.Content);
        Assert.Contains("b2", tables.Content);

        await RunAsync(new BrowserCloseTool(_rig.Sessions), context, "{}");
        var after = await new BrowserSnapshotTool(_rig.Sessions).ExecuteAsync("{}", context, CancellationToken.None);
        Assert.True(after.IsError);   // 這則對話的分頁已經關了
    }

    /// <summary>tables 模式不會用到內容轉換器；這裡給一個什麼都不做的。</summary>
    private sealed class FakeExtractor : Lumi.Core.Web.IWebContentExtractor
    {
        public Lumi.Core.Web.WebPage Extract(string html, Uri baseUrl, bool readable) => new(null, "", [], false);

        public string DecodeText(byte[] body, string? charset, bool isHtml) => "";
    }
}
