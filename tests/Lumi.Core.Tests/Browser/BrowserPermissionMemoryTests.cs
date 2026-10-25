using Lumi.Core.Tools;

namespace Lumi.Core.Tests.Browser;

public class BrowserPermissionMemoryTests
{
    private static ToolDescriptor Browser(string name, ToolPermission permission) =>
        new(name, name, "測試用瀏覽器工具。", permission, ToolModes.Workbench, TimeSpan.FromSeconds(5), Group: ToolGroups.Browser);

    private static ToolDescriptor Builtin(string name, ToolPermission permission) =>
        new(name, name, "測試用內建工具。", permission, ToolModes.Both, TimeSpan.FromSeconds(5));

    [Fact]
    public void Allowing_browser_click_also_allows_browser_type()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Browser("browser_click", ToolPermission.Write));

        Assert.True(memory.IsAllowed("c", Browser("browser_type", ToolPermission.Write)));
    }

    [Fact]
    public void Allowing_browser_write_does_not_allow_builtin_write()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Browser("browser_click", ToolPermission.Write));

        Assert.False(memory.IsAllowed("c", Builtin("write_file", ToolPermission.Write)));
        Assert.False(memory.IsAllowed("c", ToolPermission.Write));
    }

    [Fact]
    public void Allowing_builtin_write_does_not_allow_browser_write()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Builtin("write_file", ToolPermission.Write));

        Assert.False(memory.IsAllowed("c", Browser("browser_click", ToolPermission.Write)));
    }

    [Fact]
    public void Browser_network_and_write_are_remembered_separately()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Browser("browser_open", ToolPermission.Network));

        Assert.True(memory.IsAllowed("c", Browser("browser_open", ToolPermission.Network)));
        Assert.False(memory.IsAllowed("c", Browser("browser_click", ToolPermission.Write)));
    }

    [Fact]
    public void Browser_memory_is_per_conversation()
    {
        var memory = new ToolPermissionMemory();

        memory.Allow("c", Browser("browser_click", ToolPermission.Write));

        Assert.False(memory.IsAllowed("other", Browser("browser_click", ToolPermission.Write)));
    }

    [Fact]
    public void Browser_readonly_is_always_allowed()
    {
        var memory = new ToolPermissionMemory();

        Assert.True(memory.IsAllowed("c", Browser("browser_close", ToolPermission.ReadOnly)));
    }

    [Fact]
    public void Browser_destructive_is_never_remembered()
    {
        var memory = new ToolPermissionMemory();
        var destroy = Browser("browser_destroy", ToolPermission.Destructive);

        memory.Allow("c", destroy);

        Assert.False(memory.IsAllowed("c", destroy));
    }

    [Fact]
    public void Clear_forgets_browser_memory()
    {
        var memory = new ToolPermissionMemory();
        var click = Browser("browser_click", ToolPermission.Write);
        memory.Allow("c", click);

        memory.Clear("c");

        Assert.False(memory.IsAllowed("c", click));
    }
}
