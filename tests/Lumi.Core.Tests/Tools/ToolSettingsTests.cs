using Lumi.Core.Models;

namespace Lumi.Core.Tests.Tools;

public class ToolSettingsTests
{
    [Fact]
    public void Normalize_SortsDeduplicatesAndDropsBlanks()
    {
        var settings = new ToolSettings { Disabled = ["b", "a", "b", " ", ""], AlwaysAllow = ["z", " y "] }.Normalize();

        Assert.Equal(["a", "b"], settings.Disabled);
        Assert.Equal(["y", "z"], settings.AlwaysAllow);
    }

    [Fact]
    public void Default_IsEmpty()
    {
        Assert.Empty(ToolSettings.Default.Disabled);
        Assert.Empty(ToolSettings.Default.AlwaysAllow);
    }
}
