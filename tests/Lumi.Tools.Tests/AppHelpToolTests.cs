using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Core.Updates;

namespace Lumi.Tools.Tests;

public sealed class AppHelpToolTests
{
    private static readonly string[] GuideIds =
    [
        "browser", "chat", "commands", "connections", "files", "getting-started", "images", "mcp", "memory", "network",
        "personalize", "privacy", "reminders", "skills", "sprite", "tarot", "tools", "troubleshooting", "usage-cost", "voice",
    ];

    private readonly ToolContext _context = new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    private static string GuideUrl(string id) =>
        $"https://github.com/NEILLOG/Lumi/blob/v{AppVersion.Current}/docs/guide/{id}.md";

    [Fact]
    public async Task NoTopic_ListsEveryGuideWithVersion()
    {
        var result = await new AppHelpTool().ExecuteAsync("{}", _context, default);

        Assert.False(result.IsError);
        var firstLine = result.Content.Split('\n')[0];
        Assert.Contains(AppVersion.Current, firstLine);
        foreach (var id in GuideIds)
            Assert.Contains(id + "：", result.Content);
    }

    [Fact]
    public async Task BlankTopic_ListsTheDirectory()
    {
        var blank = await new AppHelpTool().ExecuteAsync("""{"topic":"   "}""", _context, default);
        var none = await new AppHelpTool().ExecuteAsync("{}", _context, default);

        Assert.Equal(none.Content, blank.Content);
    }

    [Fact]
    public async Task Topic_ReturnsTheWholeGuide_WithTheVersionedLink()
    {
        var result = await new AppHelpTool().ExecuteAsync("""{"topic":"reminders"}""", _context, default);

        Assert.False(result.IsError);
        var title = UserGuide.Find("reminders")!.Title;
        Assert.Contains(title, result.Content);
        Assert.Equal("線上版：" + GuideUrl("reminders"), result.Content.Split('\n')[^1]);
    }

    [Theory]
    [InlineData("Reminders.md")]
    [InlineData(" REMINDERS ")]
    public async Task Topic_IsCaseInsensitive_AndAcceptsMdSuffix(string topic)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { topic });

        var result = await new AppHelpTool().ExecuteAsync(json, _context, default);

        Assert.False(result.IsError);
        Assert.Contains(UserGuide.Find("reminders")!.Title, result.Content);
    }

    [Fact]
    public void Topic_StripsImages()
    {
        var text = UserGuide.StripImages("前文 ![圖](../images/a.png) 中間 [連結](a.md) 後文");

        Assert.DoesNotContain("![圖]", text);
        Assert.DoesNotContain("a.png", text);
        Assert.Contains("[連結](a.md)", text);
    }

    [Fact]
    public async Task UnknownTopic_IsAnError_AndListsTopics()
    {
        var result = await new AppHelpTool().ExecuteAsync("""{"topic":"no-such-topic"}""", _context, default);

        Assert.True(result.IsError);
        Assert.Contains("reminders", result.Content);
    }

    [Fact]
    public void EmbeddedGuides_MatchTheFilesOnDisk()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumi.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var onDisk = Directory.GetFiles(Path.Combine(dir.FullName, "docs", "guide"), "*.md")
            .Where(f => !string.Equals(Path.GetFileName(f), "README.md", StringComparison.Ordinal))
            .Count();

        Assert.Equal(onDisk, UserGuide.Topics.Count);
        Assert.Equal(GuideIds.Length, UserGuide.Topics.Count);
    }
}
