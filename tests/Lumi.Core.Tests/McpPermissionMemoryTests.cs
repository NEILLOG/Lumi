using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests;

public class McpPermissionMemoryTests
{
    private static ToolDescriptor Mcp(string name, ToolPermission permission) =>
        new(name, name, "測試用 MCP 工具。", permission, ToolModes.Workbench, TimeSpan.FromSeconds(5), Group: "mcp:a");

    private static ToolDescriptor Builtin(string name, ToolPermission permission) =>
        new(name, name, "測試用內建工具。", permission, ToolModes.Both, TimeSpan.FromSeconds(5));

    [Fact]
    public void Allowing_one_mcp_tool_does_not_allow_another_mcp_tool()
    {
        var memory = new ToolPermissionMemory();
        var read = Mcp("mcp__a__read", ToolPermission.Write);
        var write = Mcp("mcp__a__write", ToolPermission.Write);

        memory.Allow("c", read);

        Assert.True(memory.IsAllowed("c", read));
        Assert.False(memory.IsAllowed("c", write));
    }

    [Fact]
    public void Allowing_builtin_write_does_not_allow_mcp_write()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Builtin("write_file", ToolPermission.Write));

        Assert.False(memory.IsAllowed("c", Mcp("mcp__a__write", ToolPermission.Write)));
    }

    [Fact]
    public void Allowing_mcp_write_does_not_allow_builtin_write()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Mcp("mcp__a__write", ToolPermission.Write));

        Assert.False(memory.IsAllowed("c", Builtin("write_file", ToolPermission.Write)));
        Assert.False(memory.IsAllowed("c", ToolPermission.Write));
    }

    [Fact]
    public void Mcp_memory_is_per_conversation()
    {
        var memory = new ToolPermissionMemory();
        var read = Mcp("mcp__a__read", ToolPermission.Write);

        memory.Allow("c", read);

        Assert.False(memory.IsAllowed("other", read));
    }

    [Fact]
    public void Builtin_level_memory_is_unchanged()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Builtin("write_file", ToolPermission.Write));

        Assert.True(memory.IsAllowed("c", Builtin("other_write", ToolPermission.Write)));
        Assert.True(memory.IsAllowed("c", ToolPermission.Write));
        Assert.False(memory.IsAllowed("c", ToolPermission.Network));
    }

    [Fact]
    public void Mcp_destructive_is_never_remembered()
    {
        var memory = new ToolPermissionMemory();
        var destroy = Mcp("mcp__a__destroy", ToolPermission.Destructive);

        memory.Allow("c", destroy);

        Assert.False(memory.IsAllowed("c", destroy));
    }

    [Fact]
    public void Mcp_readonly_is_always_allowed()
    {
        var memory = new ToolPermissionMemory();

        Assert.True(memory.IsAllowed("c", Mcp("mcp__a__read", ToolPermission.ReadOnly)));
    }

    [Fact]
    public void Clear_forgets_mcp_memory_too()
    {
        var memory = new ToolPermissionMemory();
        var read = Mcp("mcp__a__read", ToolPermission.Write);
        memory.Allow("c", read);

        memory.Clear("c");

        Assert.False(memory.IsAllowed("c", read));
    }
}
