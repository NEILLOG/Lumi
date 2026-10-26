using Lumi.Core.Documents;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Tests.Browser;
using Lumi.Tools.Tests.Files;
using Lumi.Tools.Tests.Memory;
using Lumi.Tools.Tests.Notes;
using Lumi.Tools.Tests.Reminders;
using Lumi.Tools.Tests.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tools.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo? local = null) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => local ?? TimeZoneInfo.Utc;
}

public sealed class ToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumi-tools-" + Guid.NewGuid().ToString("N"));
    private readonly ToolContext _context = new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    // ---- calculate ----

    [Fact]
    public async Task Calculate_ReturnsResultAndSummary()
    {
        var result = await new CalculateTool().ExecuteAsync("""{"expression":"(1+2)*3"}""", _context, default);

        Assert.False(result.IsError);
        Assert.Equal("9", result.Content);
        Assert.Equal("= 9", result.Summary);
    }

    [Fact]
    public async Task Calculate_BadExpression_IsErrorResult_NotException()
    {
        var result = await new CalculateTool().ExecuteAsync("""{"expression":"1/0"}""", _context, default);

        Assert.True(result.IsError);
        Assert.Contains("除以零", result.Content);
    }

    [Fact]
    public async Task Calculate_MissingArgument_IsErrorResult()
    {
        var result = await new CalculateTool().ExecuteAsync("{}", _context, default);

        Assert.True(result.IsError);
    }

    [Fact]
    public void Calculate_PreviewShowsExpression() =>
        Assert.Equal("1+1", new CalculateTool().Preview("""{"expression":" 1+1 "}""", _context).Target);

    [Fact]
    public async Task Calculate_FloatingPointNoise_IsTrimmed()
    {
        var result = await new CalculateTool().ExecuteAsync("""{"expression":"0.1+0.2"}""", _context, default);

        Assert.Equal("0.3", result.Content);
    }

    // ---- get_current_time ----

    [Fact]
    public async Task Time_WithZone_UsesInjectedClock()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 19, 6, 32, 0, TimeSpan.Zero));
        var context = _context with { Time = clock };

        var result = await new GetCurrentTimeTool().ExecuteAsync("""{"timezone":"Asia/Taipei"}""", context, default);

        Assert.False(result.IsError);
        Assert.Contains("2026-10-19 14:32:00", result.Content);
        Assert.Contains("UTC+08:00", result.Content);
        Assert.Equal("2026-10-19 14:32（Asia/Taipei）", result.Summary);
    }

    [Fact]
    public async Task Time_NoZone_UsesLocalZone()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        
        var result = await new GetCurrentTimeTool().ExecuteAsync("{}", _context with { Time = clock }, default);

        Assert.Contains("2025-12-31 19:00", result.Content);
        Assert.Contains("UTC-05:00", result.Content);
    }

    [Fact]
    public async Task Time_UnknownZone_IsErrorResult()
    {
        var result = await new GetCurrentTimeTool().ExecuteAsync("""{"timezone":"Mars/Olympus"}""", _context, default);

        Assert.True(result.IsError);
        Assert.Contains("Mars/Olympus", result.Content);
    }

    // ---- 註冊 ----

    [Fact]
    public void AddLumiTools_RegistersAllTools_AndPassesRegistryValidation()
    {
        var services = new ServiceCollection();
        // 筆記搜尋工具需要對話儲存（App 由 Lumi.Infrastructure 註冊）；這裡用測試替身
        services.AddSingleton<IConversationStore>(new FakeSearchConversations());
        services.AddSingleton<IDocumentReader>(new FakeDocumentReader());
        services.AddFakeWebServices();
        services.AddFakeBrowserServices();
        ReminderRig.RegisterFor(services);
        MemoryRegistration.RegisterFor(services);
        services.AddLumiTools();
        using var provider = services.BuildServiceProvider();

        var registry = new ToolRegistry(provider.GetServices<ITool>());

        Assert.Equal(["add_note", "browser_back", "browser_click", "browser_close", "browser_download", "browser_extract", "browser_open", "browser_press", "browser_screenshot", "browser_scroll", "browser_select", "browser_snapshot", "browser_type", "browser_upload", "browser_wait_for_user", "calculate", "cancel_reminder", "conversation_search", "copy_file", "create_folder", "create_reminder", "download_file", "get_current_time", "http_request", "http_send", "list_dir",
                "list_reminders", "memory_delete", "memory_save", "memory_update", "move_file", "net_check", "overwrite_file", "read_document", "read_file", "read_notes", "rss_read", "search", "set_reminder_enabled", "show_details", "start_timer", "web_crawl", "web_fetch", "web_links", "write_file"],
            registry.GetAll().Select(t => t.Descriptor.Name));
        Assert.Equal(["add_note", "calculate", "cancel_reminder", "conversation_search", "create_reminder", "get_current_time", "list_dir", "list_reminders", "memory_delete", "memory_save", "memory_update", "net_check", "read_document", "read_file",
                "read_notes", "rss_read", "search", "set_reminder_enabled", "show_details", "start_timer", "web_fetch", "web_links"],
            registry.GetAll().Where(t => t.Descriptor.Modes.Allows(ChatMode.Sprite)).Select(t => t.Descriptor.Name));
    }

    // ---- show_details ----

    private readonly ToolContext _spriteContext = new("c1", ChatMode.Sprite, TimeProvider.System, EmptyFileScope.Instance);

    [Fact]
    public void ShowDetails_IsSpriteOnly_ReadOnly_NoScopeNeeded()
    {
        var d = new ShowDetailsTool().Descriptor;

        Assert.Equal(ToolModes.Sprite, d.Modes);
        Assert.True(d.Modes.Allows(ChatMode.Sprite));
        Assert.False(d.Modes.Allows(ChatMode.Workbench));   // 工作台執行時，對話迴圈會直接回錯誤
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.False(d.RequiresScope);
    }

    [Fact]
    public async Task ShowDetails_AcknowledgesAndTellsModelToSummarize()
    {
        var tool = new ShowDetailsTool();
        const string markdown = "## 範例\n```csharp\nFile.ReadAllText(path);\n```";
        var json = Json(new { title = "  C# 讀檔範例 ", markdown });

        var result = await tool.ExecuteAsync(json, _spriteContext, default);

        Assert.False(result.IsError);
        Assert.Contains("工作台", result.Content);
        Assert.Contains("一句話", result.Content);
        Assert.Equal($"{markdown.Length} 字", result.Summary);   // 字數，不是複述內容
        Assert.Equal("C# 讀檔範例", tool.Preview(json, _spriteContext).Target);   // 卡片標題：整理完整內容 · C# 讀檔範例
    }

    [Theory]
    [InlineData("""{"title":"","markdown":"內容"}""")]
    [InlineData("""{"title":"標題","markdown":"   "}""")]
    [InlineData("""{"title":"標題"}""")]
    public async Task ShowDetails_MissingTitleOrContent_IsError(string json)
    {
        var result = await new ShowDetailsTool().ExecuteAsync(json, _spriteContext, default);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task ShowDetails_LengthLimits()
    {
        var tool = new ShowDetailsTool();

        var atLimit = await tool.ExecuteAsync(Json(new { title = new string('題', ShowDetailsTool.MaxTitleLength), markdown = new string('字', ShowDetailsTool.MaxMarkdownLength) }), _spriteContext, default);
        var longTitle = await tool.ExecuteAsync(Json(new { title = new string('題', ShowDetailsTool.MaxTitleLength + 1), markdown = "x" }), _spriteContext, default);
        var longBody = await tool.ExecuteAsync(Json(new { title = "t", markdown = new string('字', ShowDetailsTool.MaxMarkdownLength + 1) }), _spriteContext, default);

        Assert.False(atLimit.IsError);
        Assert.True(longTitle.IsError);
        Assert.True(longBody.IsError);
        Assert.Contains("精簡", longBody.Content);   // 請模型自己縮短再試
    }

    [Fact]
    public void FileTools_RequireScope_WithTheirPermissions()
    {
        var services = new ServiceCollection();
        // 筆記搜尋工具需要對話儲存（App 由 Lumi.Infrastructure 註冊）；這裡用測試替身
        services.AddSingleton<IConversationStore>(new FakeSearchConversations());
        services.AddSingleton<IDocumentReader>(new FakeDocumentReader());
        services.AddFakeWebServices();
        services.AddFakeBrowserServices();
        ReminderRig.RegisterFor(services);
        MemoryRegistration.RegisterFor(services);
        services.AddLumiTools();
        using var provider = services.BuildServiceProvider();

        var fileTools = provider.GetServices<ITool>().Where(t => t.Descriptor.RequiresScope).ToList();

        Assert.Equal(["browser_download", "browser_upload", "copy_file", "create_folder", "download_file", "list_dir", "move_file", "overwrite_file", "read_document", "read_file", "search", "web_crawl", "write_file"],
            fileTools.Select(t => t.Descriptor.Name).Order());
        Assert.All(fileTools.Where(t => t.Descriptor.Name is "list_dir" or "read_file" or "search"),
            t => Assert.Equal(ToolPermission.ReadOnly, t.Descriptor.Permission));
        Assert.Equal(ToolPermission.Destructive, fileTools.Single(t => t.Descriptor.Name == "overwrite_file").Descriptor.Permission);
        Assert.All(fileTools.Where(t => t.Descriptor.Name is "write_file" or "copy_file" or "move_file" or "create_folder"),
            t => Assert.Equal(ToolPermission.Write, t.Descriptor.Permission));
    }

    [Fact]
    public void AddLumiTools_ReplacesTheEmptyScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDocumentReader>(new FakeDocumentReader());
        services.AddFakeWebServices();
        services.AddFakeBrowserServices();
        ReminderRig.RegisterFor(services);
        MemoryRegistration.RegisterFor(services);
        services.AddSingleton<IFileScopeFactory, EmptyFileScopeFactory>();
        // 筆記搜尋工具需要對話儲存（App 由 Lumi.Infrastructure 註冊）；這裡用測試替身
        services.AddSingleton<IConversationStore>(new FakeSearchConversations());
        services.AddSingleton<IDocumentReader>(new FakeDocumentReader());
        services.AddFakeWebServices();
        services.AddFakeBrowserServices();
        ReminderRig.RegisterFor(services);
        MemoryRegistration.RegisterFor(services);
        services.AddLumiTools();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<FileScopeFactory>(provider.GetRequiredService<IFileScopeFactory>());
    }

    [Fact]
    public void EveryTool_HasSchemaWithDescriptions()
    {
        var services = new ServiceCollection();
        // 筆記搜尋工具需要對話儲存（App 由 Lumi.Infrastructure 註冊）；這裡用測試替身
        services.AddSingleton<IConversationStore>(new FakeSearchConversations());
        services.AddSingleton<IDocumentReader>(new FakeDocumentReader());
        services.AddFakeWebServices();
        services.AddFakeBrowserServices();
        ReminderRig.RegisterFor(services);
        MemoryRegistration.RegisterFor(services);
        services.AddLumiTools();
        using var provider = services.BuildServiceProvider();

        foreach (var tool in provider.GetServices<ITool>())
        {
            var props = tool.InputSchema["properties"]!.AsObject();
            if (props.Count == 0)
                continue;   // 沒有參數的工具（browser_close、browser_back）：schema 是空的 properties
            Assert.NotEmpty(props);
            Assert.All(props, p => Assert.False(string.IsNullOrWhiteSpace(p.Value!["description"]?.GetValue<string>())));
        }
    }
}
