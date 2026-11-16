using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Markdown;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M36 36G 的畫面：「開支線」動作、支線的色條、出處卡片、摘要卡、剛帶回的說明。</summary>
public class BranchConversationViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);

    private static (WorkbenchWindow Window, WorkbenchViewModel Vm) Show()
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        var window = new WorkbenchWindow(vm) { Width = 1200, Height = 1000 };
        window.Show();
        window.UpdateLayout();
        return (window, vm);
    }

    private static Button ActionButton(WorkbenchWindow window) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == Strings.Branch_Start);

    private static TextBlock TextOf(WorkbenchWindow window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text);

    [AvaloniaFact]
    public void Branch_action_visibility_and_tooltip()
    {
        var (window, vm) = Show();
        var answer = MessageViewModel.From(new ChatMessage("a1", ChatRole.Assistant, [new Lumi.Core.Chat.TextBlock("回答")], DateTimeOffset.UtcNow));
        answer.ShowBranchAction = true;
        answer.CanBranch = true;
        answer.BranchTip = Strings.Branch_StartTip;
        vm.Messages.Add(answer);
        window.UpdateLayout();

        var button = ActionButton(window);
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.IsEnabled);
        Assert.Equal(Strings.Branch_StartTip, ToolTip.GetTip(button));

        answer.CanBranch = false;
        answer.BranchTip = Strings.Branch_StartBusyTip;
        window.UpdateLayout();
        Assert.False(button.IsEnabled);
        Assert.Equal(Strings.Branch_StartBusyTip, ToolTip.GetTip(button));

        answer.ShowBranchAction = false;
        window.UpdateLayout();
        Assert.False(button.IsEffectivelyVisible);   // 顯示條件在外層 Border
        window.Close();
    }

    [AvaloniaFact]
    public void Rail_only_in_branch()
    {
        var (window, _) = Show();
        var rail = window.FindByName<Border>("BranchRail");

        // 不在支線（IsInBranch 是 false）：沒有色條
        Assert.DoesNotContain("branch", rail.Classes);
        Assert.Equal(new Thickness(0), rail.BorderThickness);

        // 樣式本身：支線的色條左框 4、左內距 16、列表左邊界 4（內容往右 24）
        rail.Classes.Add("branch");
        window.UpdateLayout();
        Assert.Equal(new Thickness(4, 0, 0, 0), rail.BorderThickness);
        Assert.Equal(new Thickness(16, 0, 0, 0), rail.Padding);
        Assert.Equal(new Thickness(4, 0, 0, 0), rail.GetVisualDescendants().OfType<ItemsControl>().First().Margin);   // 4＋16＋4＝24
        window.Close();
    }

    [AvaloniaFact]
    public void Origin_card_renders_quote_highlight()
    {
        var (window, vm) = Show();
        var origin = new BranchOriginViewModel(new BranchOriginInfo(null, T0, "前面的回答開頭。選取的文字在這裡。", "選取的文字"));
        vm.Messages.Add(origin);
        window.UpdateLayout();

        var card = window.FindByName<Border>("BranchOriginCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal(origin.Eyebrow, TextOf(window, origin.Eyebrow).Text);
        Assert.Equal("選取的文字", origin.ExcerptQuote);
        var highlight = card.GetVisualDescendants().OfType<EmojiTextBlock>().Single(t => t.Text == "選取的文字");
        Assert.True(highlight.Parent is Border { IsEffectivelyVisible: true });
        Assert.Null(origin.ExtraQuoteText);
        window.Close();
    }

    [AvaloniaFact]
    public void Summary_card_toggle_and_open_command()
    {
        var (window, vm) = Show();
        var card = new BranchSummaryCardViewModel(new BranchCardInfo("b1", "標題", "摘要內容", T0, T0.AddMinutes(30), T0, 3, 0, "u1"),
            new FakeTimeProvider());
        vm.Messages.Add(card);
        window.UpdateLayout();

        var summary = window.GetVisualDescendants().OfType<MarkdownView>().Single(m => m.Markdown == "摘要內容");
        Assert.True(summary.IsEffectivelyVisible);

        card.ToggleCommand.Execute(null);
        window.UpdateLayout();
        Assert.False(summary.IsEffectivelyVisible);
        Assert.Equal(Strings.Branch_CardExpand, card.ToggleTip);

        // 「看完整支線」：綁 OpenBranchCommand，參數是支線的 id
        var open = TextOf(window, Strings.Branch_CardOpen).FindAncestorOfType<Button>(includeSelf: true)!;
        Assert.Equal("b1", open.CommandParameter);
        open.Command!.Execute(open.CommandParameter);
        Assert.NotNull(vm.OpenBranchCommand.ExecutionTask);
        window.Close();
    }

    [AvaloniaFact]
    public void Pending_caption_only_when_not_delivered()
    {
        var (window, vm) = Show();
        vm.Messages.Add(MessageViewModel.From(new ChatMessage("u1", ChatRole.User, [new Lumi.Core.Chat.TextBlock("問")], T0)));
        vm.Messages.Add(new BranchSummaryCardViewModel(new BranchCardInfo("b-pending", "待交", "摘要A", T0, T0, T0, 1, 0, null), new FakeTimeProvider()));
        vm.Messages.Add(new BranchSummaryCardViewModel(new BranchCardInfo("b-done", "已交", "摘要B", T0, T0, T0, 1, 0, "u1"), new FakeTimeProvider()));
        window.UpdateLayout();

        var captions = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == Strings.Branch_CardPending).ToList();
        Assert.Equal(2, captions.Count);
        Assert.Single(captions, c => c.IsEffectivelyVisible);
        window.Close();
    }
}
