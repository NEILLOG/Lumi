using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests;

public class ToolCatalogTests
{
    /// <summary>可以指定描述符的工具（<see cref="FakeTool"/> 沒有群組、能否關閉等參數）。</summary>
    private sealed class DescribedTool(ToolDescriptor descriptor) : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor => descriptor;

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(args.Text));
    }

    private static ToolDescriptor Desc(string name, ToolPermission permission = ToolPermission.ReadOnly, string displayName = "動作",
        string group = ToolGroups.General, bool canDisable = true, bool canAlwaysAllow = true) =>
        new(name, displayName, "測試用說明。", permission, ToolModes.Both, TimeSpan.FromSeconds(5),
            Group: group, CanDisable: canDisable, CanAlwaysAllow: canAlwaysAllow);

    private static FakeModelCatalog WithTools(ToolSettings tools) =>
        new() { Current = new FakeModelCatalog().Current with { Tools = tools } };

    [Fact]
    public async Task Disabled_tools_are_excluded_from_new_conversations()
    {
        var catalog = new ToolCatalog(new ToolRegistry([new FakeTool("alpha"), new FakeTool("beta")]),
            WithTools(new ToolSettings { Disabled = ["beta"] }));

        var enabled = await catalog.GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["alpha"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task Tools_that_cannot_be_disabled_stay_enabled()
    {
        var registry = new ToolRegistry([new DescribedTool(Desc("show_details", canDisable: false)), new FakeTool("beta")]);
        var catalog = new ToolCatalog(registry, WithTools(new ToolSettings { Disabled = ["show_details", "beta"] }));

        var enabled = await catalog.GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["show_details"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void GetAll_ignores_settings()
    {
        var catalog = new ToolCatalog(new ToolRegistry([new FakeTool("alpha"), new FakeTool("beta")]),
            WithTools(new ToolSettings { Disabled = ["alpha"] }));

        Assert.Equal(["alpha", "beta"], catalog.GetAll().Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void Groups_follow_order_and_skip_empty()
    {
        var registry = new ToolRegistry(
        [
            new DescribedTool(Desc("note_b", displayName: "Beta", group: ToolGroups.Notes)),
            new DescribedTool(Desc("note_a", displayName: "Alpha", group: ToolGroups.Notes)),
            new DescribedTool(Desc("general_one", group: ToolGroups.General)),
            new DescribedTool(Desc("sprite_one", group: ToolGroups.Sprite)),
        ]);
        var catalog = new ToolCatalog(registry, WithTools(ToolSettings.Default));

        var groups = catalog.ListGroups();

        Assert.Equal([ToolGroups.General, ToolGroups.Notes, ToolGroups.Sprite], groups.Select(g => g.Group.Id));
        Assert.Equal(["Alpha", "Beta"], groups[1].Tools.Select(t => t.Descriptor.DisplayName));
    }

    [Fact]
    public void Unknown_group_falls_back_to_general()
    {
        var registry = new ToolRegistry([new DescribedTool(Desc("weather", group: "mcp:weather"))]);
        var catalog = new ToolCatalog(registry, WithTools(ToolSettings.Default));

        var group = Assert.Single(catalog.ListGroups());

        Assert.Equal(ToolGroups.General, group.Group.Id);
        Assert.Equal("weather", Assert.Single(group.Tools).Descriptor.Name);
    }

    [Fact]
    public void CanAlwaysAllow_rules()
    {
        Assert.False(IToolCatalog.CanAlwaysAllow(Desc("r", ToolPermission.ReadOnly)));
        Assert.True(IToolCatalog.CanAlwaysAllow(Desc("w", ToolPermission.Write)));
        Assert.True(IToolCatalog.CanAlwaysAllow(Desc("n", ToolPermission.Network)));
        Assert.False(IToolCatalog.CanAlwaysAllow(Desc("d", ToolPermission.Destructive)));
        Assert.False(IToolCatalog.CanAlwaysAllow(Desc("m", ToolPermission.Write, canAlwaysAllow: false)));
    }
}
