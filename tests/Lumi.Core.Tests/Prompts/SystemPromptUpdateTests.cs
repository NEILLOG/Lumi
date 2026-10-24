using Lumi.Core.Prompts;

namespace Lumi.Core.Tests.Prompts;

public class SystemPromptUpdateTests
{
    private const string Mode = "<lumi_mode>\n目前模式：工作台\n</lumi_mode>";

    [Fact]
    public void Wrap_ThenLastIn_ReturnsTheSystemUnchanged_IncludingNewlinesAndBlankLines()
    {
        var system = "基底\n\n人設：叫我 Neil\n\n<lumi_memory>\n[m1] 記憶\n</lumi_memory>";

        var suffix = Mode + "\n\n" + SystemPromptUpdate.Wrap(system);

        Assert.Equal(system, SystemPromptUpdate.LastIn([suffix]));
    }

    [Fact]
    public void LastIn_TakesTheNewestUpdate_AndSkipsMessagesWithoutOne()
    {
        string?[] suffixes = [SystemPromptUpdate.Wrap("第一版"), null, Mode, SystemPromptUpdate.Wrap("第二版"), Mode];

        Assert.Equal("第二版", SystemPromptUpdate.LastIn(suffixes));
    }

    [Fact]
    public void LastIn_WithoutAnyUpdate_IsNull()
    {
        Assert.Null(SystemPromptUpdate.LastIn([null, Mode, ""]));
        Assert.Null(SystemPromptUpdate.LastIn([]));
    }

    [Fact]
    public void LastIn_ASystemThatMentionsTheClosingTag_StillComesBackWhole()
    {
        var system = "人設寫了 </lumi_system_update> 這個字串也不能把內容截斷";

        Assert.Equal(system, SystemPromptUpdate.LastIn([SystemPromptUpdate.Wrap(system)]));
    }
}
