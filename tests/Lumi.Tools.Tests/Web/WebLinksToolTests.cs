using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Lumi.Tools.Web;
using Xunit;

namespace Lumi.Tools.Tests.Web;

public sealed class WebLinksToolTests
{
    private readonly FakeWebClient _web = new();
    private readonly FakeWebContentExtractor _extractor = new();

    private WebLinksTool Tool => new(_web, _extractor);

    private static ToolContext Context => new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private Task<ToolResult> Links(object args) => Tool.ExecuteAsync(Json(args), Context, default);

    private void SetLinks(params WebLink[] links) =>
        _extractor.Page = new("連結頁", "", links, false);

    private static WebLink Link(string text, string url) => new(text, new Uri(url));

    private static readonly WebLink[] SampleLinks =
    [
        Link("首頁", "https://example.com/"),
        Link("新聞 Report", "https://example.com/news"),
        Link("外部文件", "https://other.org/x"),
        Link("Docs", "https://example.com/docs/guide"),
    ];

    // ================= Descriptor =================

    [Fact]
    public void Descriptor_IsNetworkBothModes_With60SecondTimeout_InWebGroup()
    {
        var d = Tool.Descriptor;

        Assert.Equal("web_links", d.Name);
        Assert.Equal("列出網頁連結", d.DisplayName);
        Assert.Equal(ToolPermission.Network, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Web, d.Group);
        Assert.Equal("列出網頁上的連結。", d.Summary);
        Assert.Contains("same_site", d.Description);
    }

    // ================= 列出與篩選 =================

    [Fact]
    public async Task ListsAllLinks_AsMarkdownBullets_InsideWebContent()
    {
        SetLinks(SampleLinks);

        var result = await Links(new { url = "https://example.com/" });

        Assert.False(result.IsError);
        Assert.StartsWith("<web_content url=\"https://example.com/\" title=\"連結頁\">", result.Content);
        Assert.Contains("- [首頁](https://example.com/)", result.Content);
        Assert.Contains("- [外部文件](https://other.org/x)", result.Content);
        Assert.Contains("共 4 個連結，列出 4 個。", result.Content);
        Assert.Equal("4 個連結", result.Summary);
    }

    [Fact]
    public async Task Filter_MatchesTextOrUrl_CaseInsensitive()
    {
        SetLinks(SampleLinks);

        var byText = await Links(new { url = "https://example.com/", filter = "REPORT" });
        var byUrl = await Links(new { url = "https://example.com/", filter = "OTHER.org" });
        var byLowerText = await Links(new { url = "https://example.com/", filter = "docs" });

        Assert.Contains("- [新聞 Report](https://example.com/news)", byText.Content);
        Assert.DoesNotContain("首頁", byText.Content);
        Assert.Contains("- [外部文件](https://other.org/x)", byUrl.Content);
        Assert.Contains("共 1 個連結，列出 1 個。", byUrl.Content);
        Assert.Contains("- [Docs](https://example.com/docs/guide)", byLowerText.Content);
    }

    [Fact]
    public async Task SameSite_KeepsOnlyLinksOnThisHost()
    {
        SetLinks(SampleLinks);

        var result = await Links(new { url = "https://example.com/", same_site = true });

        Assert.DoesNotContain("other.org", result.Content);
        Assert.Contains("共 3 個連結，列出 3 個。", result.Content);
    }

    [Fact]
    public async Task Limit_CapsTheListButReportsTheTotal()
    {
        SetLinks(SampleLinks);

        var result = await Links(new { url = "https://example.com/", limit = 2 });

        Assert.Contains("共 4 個連結，列出 2 個。", result.Content);
        Assert.Contains("- [首頁]", result.Content);
        Assert.DoesNotContain("- [Docs]", result.Content);
        Assert.Equal("2 個連結", result.Summary);
    }

    [Fact]
    public async Task DefaultLimit_Is50()
    {
        SetLinks(Enumerable.Range(1, 60).Select(i => Link($"連結{i}", $"https://example.com/{i}")).ToArray());

        var result = await Links(new { url = "https://example.com/" });

        Assert.Contains("共 60 個連結，列出 50 個。", result.Content);
        Assert.Contains("https://example.com/50)", result.Content);
        Assert.DoesNotContain("https://example.com/51)", result.Content);
    }

    [Fact]
    public async Task Limit_IsClampedToRange()
    {
        SetLinks(SampleLinks);

        var zero = await Links(new { url = "https://example.com/", limit = 0 });

        Assert.Contains("列出 1 個。", zero.Content);
    }

    [Fact]
    public async Task NoMatches_SaysSo()
    {
        SetLinks(SampleLinks);

        var result = await Links(new { url = "https://example.com/", filter = "不存在" });

        Assert.False(result.IsError);
        Assert.Contains("（沒有符合的連結。）", result.Content);
        Assert.Contains("共 0 個連結，列出 0 個。", result.Content);
    }

    [Fact]
    public async Task BracketsInLinkText_BecomeFullWidth()
    {
        SetLinks(Link("[標題]", "https://example.com/a"));

        var result = await Links(new { url = "https://example.com/" });

        Assert.Contains("- [［標題］](https://example.com/a)", result.Content);
    }

    // ================= 錯誤 =================

    [Fact]
    public async Task NotHtml_IsError()
    {
        _web.Respond = _ => FakeWebClient.Text("application/json", "{}");

        var result = await Links(new { url = "https://example.com/api" });

        Assert.True(result.IsError);
        Assert.Contains("不是網頁", result.Content);
        Assert.Equal("不是網頁", result.Summary);
    }

    [Fact]
    public async Task BadUrl_IsParameterError()
    {
        var result = await Links(new { url = "ftp://example.com/" });

        Assert.True(result.IsError);
        Assert.Equal("參數不正確", result.Summary);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task WebRequestException_BecomesFailure()
    {
        _web.Throw = new WebRequestException(WebFailureKind.Timeout, "連線逾時。");

        var result = await Links(new { url = "https://example.com/" });

        Assert.True(result.IsError);
        Assert.Equal("逾時", result.Summary);
    }
}
