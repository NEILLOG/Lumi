using Lumi.Core.Chat;
using Lumi.Core.Tools;
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
    private readonly ToolsOptions _options;
    private readonly ToolContext _context = new("c1", ChatMode.Workbench, TimeProvider.System);

    public ToolTests() => _options = new ToolsOptions { NotesDirectory = Path.Combine(_dir, "notes") };

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
        Assert.Equal("1+1", new CalculateTool().Preview("""{"expression":" 1+1 "}""").Target);

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

    // ---- save_note / delete_note ----

    [Fact]
    public async Task SaveNote_WritesFile_AddsDefaultExtension()
    {
        var result = await new SaveNoteTool(_options).ExecuteAsync(Json(new { name = "購物清單", content = "牛奶" }), _context, default);

        Assert.False(result.IsError);
        Assert.Equal("牛奶", await File.ReadAllTextAsync(Path.Combine(_options.NotesDirectory!, "購物清單.md")));
        Assert.Equal("已儲存 購物清單.md", result.Summary);
    }

    [Fact]
    public async Task SaveNote_DoesNotOverwrite()
    {
        var tool = new SaveNoteTool(_options);
        await tool.ExecuteAsync(Json(new { name = "a.txt", content = "原本" }), _context, default);

        var second = await tool.ExecuteAsync(Json(new { name = "a.txt", content = "新的" }), _context, default);

        Assert.True(second.IsError);
        Assert.Equal("原本", await File.ReadAllTextAsync(Path.Combine(_options.NotesDirectory!, "a.txt")));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("sub/dir")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\x")]
    [InlineData(".hidden")]
    [InlineData("a.exe")]
    [InlineData("a.txt.exe")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\0b")]
    [InlineData("con:")]
    public async Task SaveNote_RejectsBadNames(string name)
    {
        var result = await new SaveNoteTool(_options).ExecuteAsync(Json(new { name, content = "x" }), _context, default);

        Assert.True(result.IsError);
        Assert.False(Directory.Exists(_options.NotesDirectory!) && Directory.GetFileSystemEntries(_options.NotesDirectory!).Length > 0);
        Assert.False(File.Exists(Path.Combine(_dir, "evil")));
    }

    [Fact]
    public async Task SaveNote_RejectsTooLongContent()
    {
        var result = await new SaveNoteTool(_options).ExecuteAsync(
            Json(new { name = "big", content = new string('x', NoteFiles.MaxContentChars + 1) }), _context, default);

        Assert.True(result.IsError);
    }

    [Fact]
    public void SaveNote_Descriptor_IsWriteAndWorkbenchOnly()
    {
        var d = new SaveNoteTool(_options).Descriptor;
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
    }

    [Fact]
    public void SaveNote_Preview_ShowsNormalizedNameAndSize()
    {
        var preview = new SaveNoteTool(_options).Preview(Json(new { name = "idea", content = "12345" }));

        Assert.Equal("idea.md", preview.Target);
        Assert.Contains("5 字", preview.Detail);
    }

    [Fact]
    public async Task DeleteNote_DeletesOnlyThatFile()
    {
        var save = new SaveNoteTool(_options);
        await save.ExecuteAsync(Json(new { name = "a", content = "1" }), _context, default);
        await save.ExecuteAsync(Json(new { name = "b", content = "2" }), _context, default);

        var result = await new DeleteNoteTool(_options).ExecuteAsync(Json(new { name = "a.md" }), _context, default);

        Assert.False(result.IsError);
        Assert.False(File.Exists(Path.Combine(_options.NotesDirectory!, "a.md")));
        Assert.True(File.Exists(Path.Combine(_options.NotesDirectory!, "b.md")));
    }

    [Fact]
    public async Task DeleteNote_Missing_IsErrorResult()
    {
        var result = await new DeleteNoteTool(_options).ExecuteAsync(Json(new { name = "nothing" }), _context, default);

        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..")]
    [InlineData("sub/a.md")]
    public async Task DeleteNote_CannotEscapeNotesDirectory(string name)
    {
        Directory.CreateDirectory(_options.NotesDirectory!);
        var outside = Path.Combine(_dir, "outside.md");
        await File.WriteAllTextAsync(outside, "別動我");

        var result = await new DeleteNoteTool(_options).ExecuteAsync(Json(new { name }), _context, default);

        Assert.True(result.IsError);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void DeleteNote_Descriptor_IsDestructive() =>
        Assert.Equal(ToolPermission.Destructive, new DeleteNoteTool(_options).Descriptor.Permission);

    // ---- 註冊 ----

    [Fact]
    public void AddLumiTools_RegistersAllFour_AndPassesRegistryValidation()
    {
        var services = new ServiceCollection();
        services.AddLumiTools(o => o.NotesDirectory = _options.NotesDirectory);
        using var provider = services.BuildServiceProvider();

        var registry = new ToolRegistry(provider.GetServices<ITool>());

        Assert.Equal(["calculate", "delete_note", "get_current_time", "save_note"],
            registry.GetTools(ChatMode.Workbench).Select(t => t.Descriptor.Name));
        Assert.Equal(["calculate", "get_current_time"], registry.GetTools(ChatMode.Sprite).Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void AddLumiTools_DemoToolsCanBeDisabled()
    {
        var services = new ServiceCollection();
        services.AddLumiTools(o => o.EnableDemoTools = false);
        using var provider = services.BuildServiceProvider();

        var names = provider.GetServices<ITool>().Select(t => t.Descriptor.Name).Order().ToList();

        Assert.Equal(["calculate", "get_current_time"], names);
    }

    [Fact]
    public void EveryTool_HasSchemaWithDescriptions()
    {
        var services = new ServiceCollection();
        services.AddLumiTools();
        using var provider = services.BuildServiceProvider();

        foreach (var tool in provider.GetServices<ITool>())
        {
            var props = tool.InputSchema["properties"]!.AsObject();
            Assert.NotEmpty(props);
            Assert.All(props, p => Assert.False(string.IsNullOrWhiteSpace(p.Value!["description"]?.GetValue<string>())));
        }
    }
}
