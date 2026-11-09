using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Commands;
using Lumi.Core.Tools;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests;

/// <summary>M31 設計 6：<c>run_command</c> 的指令確認卡（說明、完整指令、執行位置、風險、沙盒提醒；只有「允許執行」與「拒絕」）。</summary>
public class ToolCardCommandTests
{
    private const string Args = """{"command":"npm install left-pad","shell":"bash"}""";
    private const string Command = "npm install left-pad";

    private sealed class Responder : IToolApprovalResponder
    {
        public List<(string CallId, ApprovalAnswer Answer)> Answers { get; } = [];

        public void Resolve(string callId, ApprovalDecision decision) => Answers.Add((callId, new ApprovalAnswer(decision)));

        public void Resolve(string callId, ApprovalAnswer answer) => Answers.Add((callId, answer));
    }

    private static CommandProposal Proposal(IReadOnlyList<CommandRisk>? risks = null, bool scratch = false) =>
        new("bash", Command, "安裝 left-pad 套件", "/Users/me/work", scratch, risks ?? []);

    private static ToolApprovalRequest CreateRequest(CommandProposal? proposal = null) =>
        new("c1", "run_command", "執行指令", ToolPermission.Write,
            new ToolPreview("bash", "執行指令", Command: proposal ?? Proposal()), Args)
        {
            AllowRemember = true,
        };

    private static ToolCardViewModel CreateCard(Responder? responder = null) =>
        new("c1", "run_command", "執行指令", ToolPermission.Write, "bash", Args,
            responder ?? new Responder(), new FakeClipboardService(), new FakeTimeProvider());

    [Fact]
    public void Command_request_shows_command_card_not_generic()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());

        Assert.True(card.ShowCommandConfirm);
        Assert.False(card.ShowGenericConfirm);
        Assert.False(card.ShowSkillConfirm);
        Assert.False(card.ShowScheduleConfirm);
        Assert.Equal(Command, card.CommandText);
        Assert.Equal("安裝 left-pad 套件", card.CommandDescription);
        Assert.Equal(string.Format(Strings.Command_FieldCommand, "bash"), card.CommandFieldLabel);
    }

    [Fact]
    public void Plain_request_still_uses_generic_confirm()
    {
        var card = CreateCard();
        card.SetAwaiting(new ToolApprovalRequest("c1", "run_command", "執行指令", ToolPermission.Write,
            new ToolPreview("bash", "執行指令"), Args));

        Assert.False(card.ShowCommandConfirm);
        Assert.True(card.ShowGenericConfirm);
    }

    [Fact]
    public void Risk_texts_follow_enum_order()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal([CommandRisk.Network, CommandRisk.Deletes, CommandRisk.ChangesSystem])));

        Assert.True(card.HasCommandRisks);
        Assert.Equal(
            [Strings.Command_RiskDeletes, Strings.Command_RiskNetwork, Strings.Command_RiskSystem],
            card.CommandRiskTexts);
    }

    [Fact]
    public void Scratch_location_has_suffix()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(scratch: true)));

        Assert.Equal("/Users/me/work" + Strings.Command_ScratchSuffix, card.CommandLocation);
    }

    [Fact]
    public void Ordinary_location_has_no_suffix()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(scratch: false)));

        Assert.Equal("/Users/me/work", card.CommandLocation);
    }

    [Fact]
    public void Allow_button_allows_once()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());

        card.AllowOnceCommand.Execute(null);

        var (callId, answer) = Assert.Single(responder.Answers);
        Assert.Equal("c1", callId);
        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Equal(ToolApproval.AllowedOnce, card.Approval);
    }

    [Fact]
    public void No_allow_for_conversation_button()
    {
        // 指令請求不走一般確認區（那裡才有「本次對話都允許」），所以這個按鈕在指令卡上不會出現
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());

        Assert.False(card.ShowGenericConfirm);
    }

    [AvaloniaFact]
    public void Headless_command_card_hides_the_conversation_button()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());
        var (view, window) = Show(new ToolCardView(), card);

        Assert.False(view.FindControl<Button>("AllowConversationButton")!.IsEffectivelyVisible);
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible),
            b => b.Content as string == Strings.Tool_AllowConversation);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_command_card_shows_the_fields_and_only_allow_and_deny()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal([CommandRisk.InstallsPackages], scratch: true)));
        var (view, window) = Show(new ToolCardView(), card);

        var texts = Texts(view);
        Assert.Contains(Strings.Command_ConfirmTitle, texts);
        Assert.Contains(Strings.Command_DescriptionNote, texts);
        Assert.Contains(Command, texts);
        Assert.Contains(Strings.Command_RiskInstalls, texts);
        Assert.Contains(Strings.Command_NotSandbox, texts);

        Assert.True(view.FindControl<Button>("CommandAllowButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("CommandDenyButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("AllowOnceButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<ItemsControl>("ApprovalWarnings")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_allow_button_is_bound_to_allow_once()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());
        var (view, window) = Show(new ToolCardView(), card);

        var allow = view.FindControl<Button>("CommandAllowButton")!;
        Assert.Same(card.AllowOnceCommand, allow.Command);
        allow.Command!.Execute(null);

        var (_, answer) = Assert.Single(responder.Answers);
        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        window.Close();
    }

    private static IEnumerable<string> Texts(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    private static (T View, Window Window) Show<T>(T view, object dataContext) where T : Control
    {
        view.DataContext = dataContext;
        var window = new Window { Width = 700, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }
}
