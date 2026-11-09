using Lumi.Core.Browser;
using Lumi.Core.Diagrams;
using Lumi.Browser.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Browser.Tests;

/// <summary>
/// 流程圖畫成圖片。沒有瀏覽器、太長的程式碼不需要真瀏覽器；真的畫圖需要 <c>LUMI_BROWSER_TESTS=1</c> 與電腦上的 Edge／Chrome。
/// </summary>
public sealed class MermaidRendererTests
{
    private sealed class NoBrowsers : IBrowserLocator
    {
        public IReadOnlyList<InstalledBrowser> Detect() => [];

        public BrowserAvailability Resolve(BrowserChoice choice) => new(null, [], "沒有");
    }

    private static void SkipWithoutBrowser() =>
        Skip.IfNot(Environment.GetEnvironmentVariable("LUMI_BROWSER_TESTS") == "1" && TestBrowsers.Instance.Detect().Count > 0,
            "需要 LUMI_BROWSER_TESTS=1 且電腦上有 Edge 或 Chrome。");

    [Fact]
    public async Task WithoutABrowser_SaysEdgeOrChromeIsNeeded()
    {
        await using var renderer = new MermaidRenderer(new NoBrowsers(), NullLogger<MermaidRenderer>.Instance);

        var result = await renderer.RenderMermaidAsync("graph TD; A-->B", dark: false);

        Assert.False(result.Succeeded);
        Assert.Equal(MermaidRenderer.NoBrowserProblem, result.Problem);
    }

    [Fact]
    public async Task TooLongCode_IsRefused()
    {
        await using var renderer = new MermaidRenderer(new NoBrowsers(), NullLogger<MermaidRenderer>.Instance);

        var result = await renderer.RenderMermaidAsync(new string('a', MermaidRenderer.MaxCodeLength + 1), dark: false);

        Assert.Equal(MermaidRenderer.TooLongProblem, result.Problem);
    }

    [Fact]
    public void ScriptIsBundled()
    {
        using var stream = typeof(MermaidRenderer).Assembly.GetManifestResourceStream("Lumi.Browser.mermaid.min.js");
        Assert.NotNull(stream);
        Assert.True(stream.Length > 1_000_000);
    }

    [SkippableFact]
    public async Task Flowchart_IsRenderedAsPng_InLightAndDark()
    {
        SkipWithoutBrowser();
        await using var renderer = new MermaidRenderer(TestBrowsers.Instance, NullLogger<MermaidRenderer>.Instance);
        const string code = "flowchart TD\n  A[收到需求] --> B{要不要開會?}\n  B -->|要| C[排會議]\n  B -->|不要| D[直接做]";

        var light = await renderer.RenderMermaidAsync(code, dark: false);
        var dark = await renderer.RenderMermaidAsync(code, dark: true);

        foreach (var result in new[] { light, dark })
        {
            Assert.True(result.Succeeded, result.Problem);
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], result.Image!.Png.Take(4));   // PNG 檔頭
            Assert.True(result.Image.Width > 50 && result.Image.Height > 50);
        }
    }

    [SkippableFact]
    public async Task SyntaxError_IsReportedAndTheNextDiagramStillWorks()
    {
        SkipWithoutBrowser();
        await using var renderer = new MermaidRenderer(TestBrowsers.Instance, NullLogger<MermaidRenderer>.Instance);

        var broken = await renderer.RenderMermaidAsync("flowchart TD\n  A -->", dark: false);
        var fine = await renderer.RenderMermaidAsync("sequenceDiagram\n  你->>Lumi: 問題\n  Lumi-->>你: 回答", dark: false);

        Assert.False(broken.Succeeded);
        Assert.StartsWith("流程圖的語法有誤", broken.Problem);
        Assert.True(fine.Succeeded, fine.Problem);
    }
}
