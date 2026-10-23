using System.Text.RegularExpressions;
using Lumi.Core.Tools;
using Lumi.Tools.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tools.Tests.Memory;

/// <summary>M14 14C 的工具總檢：權限、模式、逾時、<c>RequiresMemory</c>、說明文字與 schema（snake_case、沒有 source）。</summary>
public sealed partial class MemoryToolDescriptorTests
{
    [GeneratedRegex("^[a-z]+(_[a-z]+)*$")]
    private static partial Regex SnakeCase();

    private static readonly ITool[] Tools = [new MemorySaveTool(null!, null!, null!, null!), new MemoryUpdateTool(null!, null!, null!), new MemoryDeleteTool(null!, null!)];

    [Fact]
    public void Save_IsWrite_BothModes_RequiresMemory_NotScope()
    {
        var d = new MemorySaveTool(null!, null!, null!, null!).Descriptor;

        Assert.Equal("memory_save", d.Name);
        Assert.Equal("記住", d.DisplayName);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
        Assert.True(d.RequiresMemory);
        Assert.False(d.RequiresScope);
    }

    [Fact]
    public void Update_IsDestructive_BothModes_RequiresMemory_NotScope()
    {
        var d = new MemoryUpdateTool(null!, null!, null!).Descriptor;

        Assert.Equal("memory_update", d.Name);
        Assert.Equal("修改記憶", d.DisplayName);
        Assert.Equal(ToolPermission.Destructive, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
        Assert.True(d.RequiresMemory);
        Assert.False(d.RequiresScope);
    }

    [Fact]
    public void Delete_IsDestructive_BothModes_RequiresMemory_NotScope()
    {
        var d = new MemoryDeleteTool(null!, null!).Descriptor;

        Assert.Equal("memory_delete", d.Name);
        Assert.Equal("忘掉", d.DisplayName);
        Assert.Equal(ToolPermission.Destructive, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), d.Timeout);
        Assert.True(d.RequiresMemory);
        Assert.False(d.RequiresScope);
    }

    [Fact]
    public void Save_DescriptionDistinguishesAddNoteAndCreateReminder()
    {
        var description = new MemorySaveTool(null!, null!, null!, null!).Descriptor.Description;

        Assert.Contains("add_note", description);
        Assert.Contains("create_reminder", description);
        Assert.Contains("200 字", description);
    }

    [Fact]
    public void Update_And_Delete_DescriptionsRequireExistingId()
    {
        Assert.Contains("id", new MemoryUpdateTool(null!, null!, null!).Descriptor.Description);
        Assert.Contains("id", new MemoryDeleteTool(null!, null!).Descriptor.Description);
    }

    [Fact]
    public void AllMemoryToolNames_AreValid()
    {
        foreach (var tool in Tools)
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", tool.Descriptor.Name);
    }

    [Fact]
    public void Save_Schema_HasSnakeCaseFields_RequiredContentAndCategory_AndNoSource()
    {
        var schema = new MemorySaveTool(null!, null!, null!, null!).InputSchema;

        var properties = PropertyNames(schema);
        Assert.Equal(["category", "content", "expires_at"], properties.Order());
        Assert.All(properties, p => Assert.Matches(SnakeCase(), p));
        Assert.DoesNotContain("source", properties);
        Assert.Equal(["category", "content"], Required(schema).Order());
    }

    [Fact]
    public void Update_Schema_HasSnakeCaseFields_RequiredIdOnly()
    {
        var schema = new MemoryUpdateTool(null!, null!, null!).InputSchema;

        var properties = PropertyNames(schema);
        Assert.Equal(["category", "content", "expires_at", "id"], properties.Order());
        Assert.All(properties, p => Assert.Matches(SnakeCase(), p));
        Assert.Equal(["id"], Required(schema));
    }

    [Fact]
    public void Delete_Schema_HasIdOnly()
    {
        var schema = new MemoryDeleteTool(null!, null!).InputSchema;

        Assert.Equal(["id"], PropertyNames(schema));
        Assert.Equal(["id"], Required(schema));
    }

    [Fact]
    public void AddLumiTools_RegistersTheThreeMemoryTools()
    {
        var services = new ServiceCollection();
        ToolsServiceCollectionExtensions.AddLumiTools(services);

        var registered = services
            .Where(d => d.ServiceType == typeof(ITool))
            .Select(d => d.ImplementationType)
            .ToHashSet();

        Assert.Contains(typeof(MemorySaveTool), registered);
        Assert.Contains(typeof(MemoryUpdateTool), registered);
        Assert.Contains(typeof(MemoryDeleteTool), registered);
    }

    private static List<string> PropertyNames(System.Text.Json.Nodes.JsonNode schema) =>
        schema["properties"]!.AsObject().Select(p => p.Key).ToList();

    private static List<string> Required(System.Text.Json.Nodes.JsonNode schema) =>
        schema["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
}
