using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests;

public class McpCatalogTests
{
    private static McpServerSettings Server(string id, string name, bool enabled = true) =>
        new(id, id, name, McpTransportKind.Stdio, "npx", [], null, [], null, [], Enabled: enabled);

    private static FakeModelCatalog WithSettings(ToolSettings? tools = null, params McpServerSettings[] servers) =>
        new() { Current = new FakeModelCatalog().Current with { Tools = tools ?? ToolSettings.Default, McpServers = servers } };

    private static ToolCatalog Catalog(FakeMcpToolSource mcp, FakeModelCatalog settings, params ITool[] builtIn) =>
        new(new ToolRegistry(builtIn), settings, mcp);

    [Fact]
    public async Task New_conversation_includes_live_mcp_tools()
    {
        var mcp = new FakeMcpToolSource();
        mcp.Tools.Add(new FakeMcpTool("mcp__a__read", "a"));
        var catalog = Catalog(mcp, WithSettings(), new FakeTool("alpha"));

        var enabled = await catalog.GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["alpha", "mcp__a__read"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task New_conversation_excludes_cached_mcp_tools()
    {
        var mcp = new FakeMcpToolSource();
        mcp.Tools.Add(new FakeMcpTool("mcp__a__read", "a", live: false));
        var catalog = Catalog(mcp, WithSettings(), new FakeTool("alpha"));

        var enabled = await catalog.GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["alpha"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task New_conversation_excludes_disabled_mcp_tools()
    {
        var mcp = new FakeMcpToolSource();
        mcp.Tools.Add(new FakeMcpTool("mcp__a__read", "a"));
        mcp.Tools.Add(new FakeMcpTool("mcp__a__write", "a"));
        var settings = WithSettings(new ToolSettings { Disabled = ["mcp__a__write"] });
        var catalog = Catalog(mcp, settings);

        var enabled = await catalog.GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["mcp__a__read"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public async Task New_conversation_waits_for_startup_before_reading_tools()
    {
        var mcp = new FakeMcpToolSource();
        var started = false;
        mcp.OnWaitForStartup = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            mcp.Tools.Add(new FakeMcpTool("mcp__a__read", "a"));   // 伺服器在等待期間連上
            started = true;
        };
        var catalog = Catalog(mcp, WithSettings());

        var enabled = await catalog.GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.True(started);
        Assert.Equal(["mcp__a__read"], enabled.Select(t => t.Descriptor.Name));
        Assert.Equal([TimeSpan.FromSeconds(10)], mcp.WaitTimeouts);
    }

    [Fact]
    public async Task New_conversation_returns_after_startup_timeout_without_waiting_for_stuck_servers()
    {
        // 逾時的行為由 McpServerManager 負責（回傳而不丟例外）；這裡模擬「回來時伺服器還沒連上」
        var mcp = new FakeMcpToolSource();
        mcp.Tools.Add(new FakeMcpTool("mcp__slow__read", "slow", live: false));
        var catalog = Catalog(mcp, WithSettings(), new FakeTool("alpha"));

        var enabled = await catalog.GetEnabledForNewConversationAsync(CancellationToken.None);

        Assert.Equal(["alpha"], enabled.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void GetAll_contains_only_builtin_tools()
    {
        var mcp = new FakeMcpToolSource();
        mcp.Tools.Add(new FakeMcpTool("mcp__a__read", "a"));
        var catalog = Catalog(mcp, WithSettings(), new FakeTool("alpha"));

        Assert.Equal(["alpha"], catalog.GetAll().Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void Find_finds_cached_mcp_tools_and_builtin_tools()
    {
        var mcp = new FakeMcpToolSource();
        mcp.Tools.Add(new FakeMcpTool("mcp__a__read", "a", live: false));
        var catalog = Catalog(mcp, WithSettings(), new FakeTool("alpha"));

        Assert.Equal("mcp__a__read", catalog.Find("mcp__a__read")?.Descriptor.Name);
        Assert.Equal("alpha", catalog.Find("alpha")?.Descriptor.Name);
        Assert.Null(catalog.Find("mcp__a__missing"));
    }

    [Fact]
    public void Mcp_groups_come_after_builtin_and_are_ordered_by_name()
    {
        var mcp = new FakeMcpToolSource();
        var settings = WithSettings(null, Server("b", "Beta"), Server("a", "Alpha"));
        var catalog = Catalog(mcp, settings, new FakeTool("alpha"));

        var groups = catalog.ListGroups();

        Assert.Equal([ToolGroups.General, "mcp:a", "mcp:b"], groups.Select(g => g.Group.Id));
        Assert.Equal([0, 100, 101], groups.Select(g => g.Group.Order));
        Assert.Equal(["Alpha", "Beta"], groups.Skip(1).Select(g => g.Group.Name));
    }

    [Fact]
    public void Disabled_servers_have_no_group()
    {
        var mcp = new FakeMcpToolSource();
        var settings = WithSettings(null, Server("a", "Alpha", enabled: false));
        var catalog = Catalog(mcp, settings, new FakeTool("alpha"));

        Assert.Equal([ToolGroups.General], catalog.ListGroups().Select(g => g.Group.Id));
    }

    [Fact]
    public void Each_mcp_tool_sits_in_its_server_group_sorted_by_display_name()
    {
        var mcp = new FakeMcpToolSource();
        mcp.Tools.Add(new FakeMcpTool("mcp__a__zeta", "a"));
        mcp.Tools.Add(new FakeMcpTool("mcp__a__alpha", "a"));
        mcp.Tools.Add(new FakeMcpTool("mcp__b__one", "b"));
        var settings = WithSettings(null, Server("a", "Alpha"), Server("b", "Beta"));
        var catalog = Catalog(mcp, settings);

        var groups = catalog.ListGroups();

        Assert.Equal(["mcp__a__alpha", "mcp__a__zeta"], groups[0].Tools.Select(t => t.Descriptor.Name));
        Assert.Equal(["mcp__b__one"], groups[1].Tools.Select(t => t.Descriptor.Name));
    }

    [Fact]
    public void Enabled_server_without_tools_still_has_an_empty_group()
    {
        var mcp = new FakeMcpToolSource();
        var catalog = Catalog(mcp, WithSettings(null, Server("a", "Alpha")));

        var group = Assert.Single(catalog.ListGroups(), g => g.Group.Id == "mcp:a");

        Assert.Empty(group.Tools);
    }
}
