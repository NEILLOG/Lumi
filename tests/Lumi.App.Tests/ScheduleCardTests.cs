using System.Globalization;
using System.Text.Json.Nodes;
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

/// <summary>
/// M24 24G：建立排程的確認區（草圖 ScheduleCreate）、排程授權的徽章與按鈕文字、排程紀錄裡的「以後都允許」。
/// </summary>
public class ScheduleCardTests
{
    private const string Args = """{"name":"每日行事曆整理","prompt":"整理今天的行程","recurrence":"daily","time":"09:00","suggested_permissions":["write","network"]}""";

    private sealed class Responder : IToolApprovalResponder
    {
        public List<(string CallId, ApprovalAnswer Answer)> Answers { get; } = [];

        public void Resolve(string callId, ApprovalDecision decision) => Answers.Add((callId, new ApprovalAnswer(decision)));

        public void Resolve(string callId, ApprovalAnswer answer) => Answers.Add((callId, answer));
    }

    private static ScheduleProposal Proposal(bool write = true, bool network = true, IReadOnlyList<string>? scope = null) =>
        new("每日行事曆整理", "每天 09:00", "2026-11-01 09:00", "整理我今天的行事曆", "預設模型（目前是 Anthropic · claude-sonnet-4-5）",
            scope ?? ["筆記資料夾"], write, network);

    private static ToolApprovalRequest CreateRequest(ScheduleProposal? proposal = null) =>
        new("c1", "create_scheduled_prompt", "建立排程", ToolPermission.Write,
            new ToolPreview("每日行事曆整理", "每天 09:00", Schedule: proposal ?? Proposal()), Args)
        {
            AllowRemember = false,   // 建立排程一定詢問，不提供「本次對話都允許」（ConversationService 設定）
        };

    private static ToolCardViewModel CreateCard(Responder? responder = null, IConfirmService? confirm = null) =>
        new("c1", "create_scheduled_prompt", "建立排程", ToolPermission.Write, "每日行事曆整理", Args,
            responder ?? new Responder(), new FakeClipboardService(), new FakeTimeProvider(), confirm);

    private static ToolCardViewModel CreatedCard(string content, ToolApproval approval = ToolApproval.AllowedOnce)
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());
        card.Complete(new ToolResultBlock("c1", content, false, "建立排程", "每日行事曆整理", "已建立排程", 120, approval));
        return card;
    }

    private const string CreatedJson = """
        {"id":"s1","name":"每日行事曆整理","next_run_at":"2026-11-01T09:00:00+08:00","next_run_at_local":"2026-11-01 09:00",
         "granted":["read","write","network"],"scope":["筆記資料夾"],"note":"紀錄在歷史對話的「排程」分頁"}
        """;

    private static string CreatedSummary() => string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Created_NextFormat,
        "2026-11-01 09:00",
        string.Join(Strings.Schedule_GrantListSeparator, Strings.Schedule_Perm_ReadOnly, Strings.Schedule_Perm_Write, Strings.Schedule_Perm_Network));

    // ---- 確認區：預先勾選、破壞性、回答 ----

    [Fact]
    public void AwaitingCreate_ShowsTheScheduleConfirm_NotTheGenericOne()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());

        Assert.True(card.ShowScheduleConfirm);
        Assert.False(card.ShowGenericConfirm);
        Assert.Equal("每日行事曆整理", card.ScheduleName);
        Assert.Equal("每天 09:00（" + string.Format(CultureInfo.CurrentCulture, Strings.Schedule_NextFormat, "2026-11-01 09:00") + "）", card.ScheduleWhen);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Create_TitleFormat, "每日行事曆整理") + " · " + Strings.Schedule_Create_Waiting,
            card.Title);
    }

    [Fact]
    public void Suggested_WriteAndNetwork_ArePrechecked_Destructive_IsNot()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(write: true, network: true)));

        Assert.True(card.ScheduleWrite);
        Assert.True(card.ScheduleNetwork);
        Assert.False(card.ScheduleDestructive);
        Assert.True(card.WriteSuggested);
        Assert.True(card.NetworkSuggested);
    }

    [Fact]
    public void NotSuggested_IsNotPrechecked()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest(Proposal(write: false, network: false)));

        Assert.False(card.ScheduleWrite);
        Assert.False(card.ScheduleNetwork);
        Assert.False(card.WriteSuggested);
    }

    [Fact]
    public void CreateSchedule_SendsTheAmendedArguments_WithTheCheckedGrants()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());

        card.CreateScheduleCommand.Execute(null);

        var (callId, answer) = Assert.Single(responder.Answers);
        Assert.Equal("c1", callId);
        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        var json = JsonNode.Parse(answer.AmendedArgumentsJson!)!.AsObject();
        Assert.Equal("每日行事曆整理", json["name"]!.GetValue<string>());   // 原本的參數保留
        Assert.Equal(new[] { "write", "network" }, json["suggested_permissions"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "write", "network" }, json["granted_permissions"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(ToolApproval.AllowedOnce, card.Approval);
    }

    [Fact]
    public void CreateSchedule_WithNothingChecked_SendsEmptyGrantArray()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());
        card.ScheduleWrite = false;
        card.ScheduleNetwork = false;

        card.CreateScheduleCommand.Execute(null);

        var json = JsonNode.Parse(Assert.Single(responder.Answers).Answer.AmendedArgumentsJson!)!.AsObject();
        Assert.Empty(json["granted_permissions"]!.AsArray());
        Assert.Empty(json["suggested_permissions"]!.AsArray());
    }

    [Fact]
    public void CreateSchedule_WithDestructiveChecked_AddsItToGrantedOnly()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());
        card.ScheduleDestructive = true;

        card.CreateScheduleCommand.Execute(null);

        var json = JsonNode.Parse(Assert.Single(responder.Answers).Answer.AmendedArgumentsJson!)!.AsObject();
        Assert.Equal(new[] { "write", "network", "destructive" }, json["granted_permissions"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "write", "network" }, json["suggested_permissions"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Destructive_SwitchesTheNote_AndKeepsItOffByDefault()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());

        Assert.Equal(Strings.Schedule_Grant_DestructiveNote, card.ScheduleDestructiveNote);
        card.ScheduleDestructive = true;
        Assert.Equal(Strings.Schedule_Grant_DestructiveOnNote, card.ScheduleDestructiveNote);
    }

    [Fact]
    public void Deny_SendsDeny()
    {
        var responder = new Responder();
        var card = CreateCard(responder);
        card.SetAwaiting(CreateRequest());

        card.DenyCommand.Execute(null);

        Assert.Equal(ApprovalDecision.Deny, Assert.Single(responder.Answers).Answer.Decision);
        Assert.Equal(ToolCardState.Denied, card.State);
        Assert.Equal(Strings.Schedule_Create_Denied, card.ApprovalNote);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Create_TitleFormat, "每日行事曆整理"), card.Title);
    }

    // ---- 建立後與拒絕後 ----

    [Fact]
    public void Created_TitleIsDone_AndSummaryShowsNextRunAndGrants()
    {
        var card = CreatedCard(CreatedJson);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Create_TitleFormat, "每日行事曆整理") + " · " + Strings.Schedule_Create_Done,
            card.Title);
        Assert.Equal(CreatedSummary(), card.ScheduleSummary);
        Assert.True(card.HasScheduleSummary);
        Assert.Equal(Strings.Tool_ApprovedOnce, card.ApprovalNote);
    }

    [Fact]
    public void Created_WithUnreadableResult_ShowsOnlyTheSummary_WithoutThrowing()
    {
        var card = CreatedCard("這不是 JSON");

        Assert.False(card.HasScheduleSummary);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Create_TitleFormat, "每日行事曆整理") + " · " + Strings.Schedule_Create_Done, card.Title);
    }

    [Fact]
    public void Reopened_FromHistory_WithoutRequest_ShowsTheCompletedState()
    {
        // 從資料庫重建：只有 ToolResultBlock，沒有 ScheduleProposal
        var card = new ToolCardViewModel("c1", "create_scheduled_prompt", "建立排程", null, "每日行事曆整理", Args,
            new Responder(), new FakeClipboardService(), new FakeTimeProvider());
        card.Complete(new ToolResultBlock("c1", CreatedJson, false, "建立排程", "每日行事曆整理", "已建立排程", 120, ToolApproval.AllowedOnce));

        Assert.False(card.ShowScheduleConfirm);
        Assert.Equal(CreatedSummary(), card.ScheduleSummary);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Create_TitleFormat, "每日行事曆整理") + " · " + Strings.Schedule_Create_Done,
            card.Title);
    }

    [Fact]
    public void Failed_Create_KeepsTheOrdinaryTitle()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());
        card.Complete(new ToolResultBlock("c1", "{}", true, "建立排程", "每日行事曆整理", "無法建立", 10, ToolApproval.AllowedOnce));

        Assert.True(card.IsFailed);
        Assert.False(card.HasScheduleSummary);
        Assert.DoesNotContain(Strings.Schedule_Create_Done, card.Title);
    }

    [Fact]
    public void ApprovedBySchedule_ShowsTheScheduleBadge()
    {
        var card = new ToolCardViewModel("c2", "add_note", "新增筆記", ToolPermission.Write, null, "{}",
            new Responder(), new FakeClipboardService(), new FakeTimeProvider());
        card.Complete(new ToolResultBlock("c2", "ok", false, "新增筆記", null, "已寫入", 5, ToolApproval.AllowedBySchedule));

        Assert.Equal(Strings.Schedule_ApprovedBySchedule, card.ApprovalNote);
        Assert.True(card.HasApprovalNote);
    }

    // ---- 排程紀錄（IsScheduledRun）：按鈕文字、排除、確認、說明 ----

    private static ToolCardViewModel RunCard(ToolPermission permission, bool excluded = false, bool allowRemember = true,
        Responder? responder = null, IConfirmService? confirm = null)
    {
        var card = new ToolCardViewModel("r1", "write_file", "寫入檔案", permission, "a.txt", "{}",
            responder ?? new Responder(), new FakeClipboardService(), new FakeTimeProvider(), confirm)
        {
            IsScheduledRun = true,
            ScheduleExcluded = excluded,
        };
        card.SetAwaiting(new ToolApprovalRequest("r1", "write_file", "寫入檔案", permission, new ToolPreview("a.txt", "寫入"), "{}")
        {
            AllowRemember = allowRemember,
        });
        return card;
    }

    [Fact]
    public void ScheduledRun_Write_ShowsAlwaysAllowLabel_AndResolvesAsAllowForConversation()
    {
        var responder = new Responder();
        var confirm = new FakeConfirmService();
        var card = RunCard(ToolPermission.Write, responder: responder, confirm: confirm);

        Assert.True(card.ShowAllowForConversation);
        Assert.Equal(Strings.Schedule_AllowAlways, card.AllowConversationLabel);
        card.AllowForConversationCommand.Execute(null);

        Assert.Equal(ApprovalDecision.AllowForConversation, Assert.Single(responder.Answers).Answer.Decision);
        Assert.Empty(confirm.Asked);   // 寫入不需要破壞性確認
    }

    [Fact]
    public void ScheduledRun_Excluded_HidesTheAlwaysAllowButton_AndChangesTheNote()
    {
        var card = RunCard(ToolPermission.Write, excluded: true);

        Assert.False(card.ShowAllowForConversation);
        Assert.Equal(Strings.Schedule_AwaitingNoteExcluded, card.AwaitingNote);
    }

    [Fact]
    public void ScheduledRun_AllowRememberFalse_HidesTheButton()
    {
        var card = RunCard(ToolPermission.Network, allowRemember: false);

        Assert.False(card.ShowAllowForConversation);
    }

    [Fact]
    public void ScheduledRun_AwaitingNote_NamesTheMissingPermission()
    {
        var card = RunCard(ToolPermission.Write);

        Assert.True(card.ShowAwaitingNote);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule_AwaitingNoteFormat, Strings.Schedule_Perm_Write), card.AwaitingNote);
    }

    [Fact]
    public void OrdinaryCard_KeepsTheOrdinaryLabel_AndNoAwaitingNote()
    {
        var card = new ToolCardViewModel("c3", "write_file", "寫入檔案", ToolPermission.Write, "a.txt", "{}",
            new Responder(), new FakeClipboardService(), new FakeTimeProvider());
        card.SetAwaiting(new ToolApprovalRequest("c3", "write_file", "寫入檔案", ToolPermission.Write, new ToolPreview("a.txt", null), "{}"));

        Assert.Equal(Strings.Tool_AllowConversation, card.AllowConversationLabel);
        Assert.False(card.ShowAwaitingNote);
        Assert.True(card.ShowAllowForConversation);
    }

    [Fact]
    public void ScheduledRun_Destructive_WithoutConfirmService_HidesTheAlwaysAllowButton()
    {
        var card = RunCard(ToolPermission.Destructive);

        Assert.False(card.ShowAllowForConversation);
    }

    [Fact]
    public void ScheduledRun_Destructive_AlwaysAllow_NeedsConfirmation_AndDoesNothingWhenCancelled()
    {
        var responder = new Responder();
        var confirm = new FakeConfirmService { Answer = false };
        var card = RunCard(ToolPermission.Destructive, responder: responder, confirm: confirm);

        Assert.True(card.ShowAllowForConversation);
        card.AllowForConversationCommand.Execute(null);

        Assert.Empty(responder.Answers);
        Assert.True(card.IsAwaiting);
        var (message, confirmText) = Assert.Single(confirm.Asked);
        Assert.Equal(Strings.Schedule_AllowAlwaysDestructiveConfirm, confirmText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Schedule_AllowAlwaysDestructiveFormat, "寫入檔案"), message);
    }

    [Fact]
    public void ScheduledRun_Destructive_AlwaysAllow_ResolvesAfterConfirmation()
    {
        var responder = new Responder();
        var confirm = new FakeConfirmService { Answer = true };
        var card = RunCard(ToolPermission.Destructive, responder: responder, confirm: confirm);

        card.AllowForConversationCommand.Execute(null);

        Assert.Equal(ApprovalDecision.AllowForConversation, Assert.Single(responder.Answers).Answer.Decision);
    }

    [Fact]
    public void ScheduledRun_AlwaysAllow_ShowsAsOnceOnTheCard()
    {
        var responder = new Responder();
        var card = RunCard(ToolPermission.Write, responder: responder);

        card.AllowForConversationCommand.Execute(null);

        Assert.Equal(ToolApproval.AllowedOnce, card.Approval);
    }

    [Fact]
    public void ScheduledRun_Destructive_NormalCard_StillHidesTheConversationButton()
    {
        var card = new ToolCardViewModel("c4", "overwrite_file", "覆寫檔案", ToolPermission.Destructive, "a.txt", "{}",
            new Responder(), new FakeClipboardService(), new FakeTimeProvider());
        card.SetAwaiting(new ToolApprovalRequest("c4", "overwrite_file", "覆寫檔案", ToolPermission.Destructive, new ToolPreview("a.txt", null), "{}"));

        Assert.False(card.ShowAllowForConversation);
    }

    // ---- 畫面 ----

    private static (T View, Window Window) Show<T>(T view, object dataContext) where T : Control
    {
        view.DataContext = dataContext;
        var window = new Window { Width = 700, Height = 900, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static IEnumerable<string> Texts(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    [AvaloniaFact]
    public void Headless_CreateConfirm_RendersWithoutErrors_AndShowsTheButtons()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());
        var (view, window) = Show(new ToolCardView(), card);

        Assert.True(view.FindControl<Button>("CreateScheduleButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("CreateScheduleDangerButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<CheckBox>("GrantWriteCheck")!.IsChecked == true);
        Assert.True(view.FindControl<CheckBox>("GrantNetworkCheck")!.IsChecked == true);
        Assert.False(view.FindControl<CheckBox>("GrantDestructiveCheck")!.IsChecked == true);
        Assert.False(view.FindControl<Button>("AllowOnceButton")!.IsEffectivelyVisible);
        Assert.Contains(Strings.Schedule_AlwaysAskNote, Texts(view));
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_CheckingDestructive_SwitchesTheButtonToDanger()
    {
        var card = CreateCard();
        card.SetAwaiting(CreateRequest());
        var (view, window) = Show(new ToolCardView(), card);

        view.FindControl<CheckBox>("GrantDestructiveCheck")!.IsChecked = true;
        view.UpdateLayout();

        Assert.False(view.FindControl<Button>("CreateScheduleButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("CreateScheduleDangerButton")!.IsEffectivelyVisible);
        Assert.Equal(Strings.Schedule_Create_ConfirmDestructive, view.FindControl<Button>("CreateScheduleDangerButton")!.Content);
        Assert.Contains(Strings.Schedule_Grant_DestructiveOnNote, Texts(view));
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_CreatedCard_ShowsTheDoneTitleAndSummary()
    {
        var card = CreatedCard(CreatedJson);
        var (view, window) = Show(new ToolCardView(), card);

        Assert.Contains(card.Title, Texts(view));
        Assert.Contains(CreatedSummary(), Texts(view));
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_ScheduledRunExcluded_ShowsTheExcludedNoteAndNoAlwaysButton()
    {
        var card = RunCard(ToolPermission.Write, excluded: true);
        var (view, window) = Show(new ToolCardView(), card);

        Assert.Contains(Strings.Schedule_AwaitingNoteExcluded, Texts(view));
        Assert.False(view.FindControl<Button>("AllowConversationButton")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Headless_ScheduledRun_ShowsTheAlwaysAllowLabel()
    {
        var card = RunCard(ToolPermission.Write);
        var (view, window) = Show(new ToolCardView(), card);

        var button = view.FindControl<Button>("AllowConversationButton")!;
        Assert.True(button.IsEffectivelyVisible);
        Assert.Equal(Strings.Schedule_AllowAlways, button.Content);
        window.Close();
    }
}
