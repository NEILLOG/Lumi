using Lumi.App.Resources;
using Lumi.App.ViewModels;

namespace Lumi.App.Tests;

/// <summary>使用者的長訊息預設收合：只顯示前幾行，按「顯示全部」展開、「收合」收回。助理的回答不收合。</summary>
public class LongUserMessageTests
{
    [Fact]
    public void ShortMessage_IsNotCollapsed()
    {
        var vm = MessageViewModel.User("幫我整理這份會議紀錄");

        Assert.False(vm.IsLongUserText);
        Assert.Equal(0, vm.UserTextMaxLines);
    }

    [Fact]
    public void ManyLines_AreCollapsed_UntilExpanded()
    {
        var text = string.Join('\n', Enumerable.Range(1, MessageViewModel.CollapseLineThreshold + 1).Select(i => $"第 {i} 行"));
        var vm = MessageViewModel.User(text);

        Assert.True(vm.IsLongUserText);
        Assert.Equal(MessageViewModel.CollapsedUserLines, vm.UserTextMaxLines);
        Assert.Equal(Strings.Message_ExpandUserText, vm.UserTextToggleText);

        vm.ToggleUserTextCommand.Execute(null);

        Assert.Equal(0, vm.UserTextMaxLines);
        Assert.Equal(Strings.Message_CollapseUserText, vm.UserTextToggleText);

        vm.ToggleUserTextCommand.Execute(null);

        Assert.Equal(MessageViewModel.CollapsedUserLines, vm.UserTextMaxLines);
    }

    [Fact]
    public void LongParagraphWithoutLineBreaks_IsCollapsed()
    {
        var vm = MessageViewModel.User(new string('字', MessageViewModel.CollapseCharThreshold + 1));

        Assert.True(vm.IsLongUserText);
    }

    [Fact]
    public void AssistantReplies_AreNeverCollapsed()
    {
        var vm = MessageViewModel.StreamingAssistant();
        vm.Text = new string('字', MessageViewModel.CollapseCharThreshold * 3);

        Assert.False(vm.IsLongUserText);
        Assert.Equal(0, vm.UserTextMaxLines);
    }
}
