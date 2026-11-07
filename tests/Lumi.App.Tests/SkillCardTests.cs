using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests;

/// <summary>M29 5.7、D8：<c>save_skill</c> 的技能確認卡（名稱、什麼時候用、步驟全文、取代警告；只有「儲存」與「不要」，不能編輯）。</summary>
public class SkillCardTests
{
    private const string Args = """{"name":"週報","description":"每週五整理本週工作時使用","instructions":"1. 列出本週完成的事\n2. 寫成三段"}""";
    private const string Body = "1. 列出本週完成的事\n2. 寫成三段";

    private sealed class Responder : IToolApprovalResponder
    {
        public List<(string CallId, ApprovalAnswer Answer)> Answers { get; } = [];

        public void Resolve(string callId, ApprovalDecision decision) => Answers.Add((callId, new ApprovalAnswer(decision)));

        public void Resolve(string callId, ApprovalAnswer answer) => Answers.Add((callId, answer));
    }

    private static SkillProposal Proposal(bool replaces = false) =>
        new("週報", "每週五整理本週工作時使用", Body, replaces);

    private static ToolApprovalRequest CreateRequest(SkillProposal? proposal = null) =>
        new("c1", "save_skill", "儲存技能", ToolPermission.Write,
            new ToolPreview("週報", "儲存技能", Skill: proposal ?? Proposal()), Args)
        {
            AllowRemember = false,   // save_skill 是 AlwaysAsk，不提供「本次對話都允許」
        };

    private static ToolCardViewModel CreateCard(Responder? responder = null) =>
        new("c1", "save_skill", "儲存技能", ToolPermission.Write, "週報", Args,
            responder ?? new Responder(), new FakeClipboardService(), new FakeTimeProvider());

    private static string ReplaceWarning(string name) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Skills_ConfirmReplaceWarningFormat, name);

    // ---- 顯示條件與內容 ----

    [Fact]
    public void AwaitingSkill_ShowsTheSkillConfirm_NotTheGenericOne()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());

        Assert.True(card.ShowSkillConfirm);
        Assert.False(card.ShowGenericConfirm);
        Assert.False(card.ShowScheduleConfirm);
        Assert.Equal("週報", card.SkillName);
        Assert.Equal("每週五整理本週工作時使用", card.SkillDescription);
        Assert.Equal(Body, card.SkillBody);
    }

    [Fact]
    public void Completed_HidesTheSkillConfirm()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());

        card.Complete(new ToolResultBlock("c1", "已存成技能", false, "儲存技能", "週報", "已存成技能", 10, ToolApproval.AllowedOnce));

        Assert.False(card.ShowSkillConfirm);
        Assert.False(card.ShowGenericConfirm);
    }

    [Fact]
    public void RequestWithoutSkill_KeepsTheGenericConfirm()
    {
        var card = CreateCard();
        card.SetAwaiting(new ToolApprovalRequest("c1", "web_fetch", "讀取網頁", ToolPermission.Network, new ToolPreview("x", "y"), "{}"));

        Assert.False(card.ShowSkillConfirm);
        Assert.True(card.ShowGenericConfirm);
    }

    [Fact]
    public void ReplacesExisting_ShowsTheWarning_WithTheSkillName()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(replaces: true)));

        Assert.True(card.ShowReplaceWarning);
        Assert.Equal(ReplaceWarning("週報"), card.ReplaceWarningText);
    }

    [Fact]
    public void NewSkill_HasNoReplaceWarning()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(replaces: false)));

        Assert.False(card.ShowReplaceWarning);
    }

    // ---- 回答 ----

    [Fact]
    public void Save_SendsAllowOnce_WithoutAmendedArguments()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());

        card.AllowOnceCommand.Execute(null);

        var (callId, answer) = Assert.Single(responder.Answers);
        Assert.Equal("c1", callId);
        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Null(answer.AmendedArgumentsJson);
        Assert.Equal(ToolApproval.AllowedOnce, card.Approval);
    }

    [Fact]
    public void Deny_SendsDeny()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());

        card.DenyCommand.Execute(null);

        var (_, answer) = Assert.Single(responder.Answers);
        Assert.Equal(ApprovalDecision.Deny, answer.Decision);
        Assert.Equal(ToolCardState.Denied, card.State);
    }

    // ---- 畫面 ----

    [AvaloniaFact]
    public void Headless_SkillCard_ShowsTheNameAndTheFullSteps_WithOnlySaveAndDeny()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());
        var (view, window) = Show(new ToolCardView(), card);

        var texts = Texts(view);
        Assert.Contains(Strings.Skills_ConfirmTitle, texts);
        Assert.Contains("週報", texts);
        Assert.Contains("每週五整理本週工作時使用", texts);
        Assert.Contains(Body, texts);

        Assert.True(view.FindControl<Button>("SaveSkillButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("SaveSkillDenyButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("AllowOnceButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("AllowConversationButton")!.IsEffectivelyVisible);
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible),
            b => b.Content as string == Strings.Tool_AllowConversation);
        Assert.False(view.FindControl<ItemsControl>("ApprovalWarnings")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_ReplacingSkill_ShowsTheWarningRow()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(replaces: true)));
        var (view, window) = Show(new ToolCardView(), card);

        Assert.Contains(ReplaceWarning("週報"), Texts(view));
        var warning = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == ReplaceWarning("週報"));
        Assert.True(warning.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_NewSkill_HasNoWarningText()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(replaces: false)));
        var (view, window) = Show(new ToolCardView(), card);

        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
            text => text == ReplaceWarning("週報"));
        window.Close();
    }

    private static (T View, Window Window) Show<T>(T view, object dataContext) where T : Control
    {
        var window = new Window { Width = 800, Height = 900, Content = view, DataContext = dataContext };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static IEnumerable<string> Texts(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");
}
