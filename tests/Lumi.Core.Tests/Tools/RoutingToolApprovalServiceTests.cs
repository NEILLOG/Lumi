using Lumi.Core.Reminders;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.Tools;

public class RoutingToolApprovalServiceTests
{
    private static readonly ScheduleGrant WriteOnly = new(Write: true, Network: false, Destructive: false);

    private static ActiveScheduledRun Run(ScheduleGrant grant) => new("run-1", "sched-1", "每天整理", grant);

    private static ToolDescriptor Desc(string name, ToolPermission permission, string group = ToolGroups.General, bool requiresMemory = false) =>
        new(name, "測試", "測試用。", permission, ToolModes.Both, TimeSpan.FromSeconds(5), RequiresMemory: requiresMemory, Group: group);

    private static ToolApprovalRequest Req(string toolName, ToolPermission permission, string? conversationId, params string[] warnings) =>
        new("call-1", toolName, "測試", permission, ToolPreview.None, "{}") { ConversationId = conversationId, Warnings = warnings };

    private static ToolCatalog Catalog(params ToolDescriptor[] descriptors) =>
        new(new ToolRegistry(descriptors.Select(d => (ITool)new DescriptorTool(d))), new FakeModelCatalog());

    private static RoutingToolApprovalService Routing(IToolApprovalService inner, FakeScheduledRunHost host, IToolCatalog catalog) =>
        new(inner, host, catalog);

    [Fact]
    public async Task Ordinary_conversation_is_passed_straight_to_the_inner_service()
    {
        var host = new FakeScheduledRunHost();
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowForConversation));
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, conversationId: null), default);

        Assert.Equal(ApprovalDecision.AllowForConversation, answer.Decision);   // 一般對話的「本次對話都允許」原樣回傳
        Assert.Single(inner.Requests);
        Assert.Empty(host.Events);
    }

    [Fact]
    public async Task Conversation_that_is_not_a_scheduled_run_is_passed_through()
    {
        var host = new FakeScheduledRunHost();
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce));
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, conversationId: "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Single(inner.Requests);
        Assert.Empty(host.Events);
    }

    [Fact]
    public async Task Inside_the_grant_returns_allow_by_schedule_without_asking()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(WriteOnly);
        var inner = new AnsweringApprovalService();
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowBySchedule, answer.Decision);
        Assert.Empty(inner.Requests);
        Assert.Empty(host.Events);
    }

    [Fact]
    public async Task Read_only_tool_in_a_scheduled_run_is_allowed()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(ScheduleGrant.None);
        var inner = new AnsweringApprovalService();
        var routing = Routing(inner, host, Catalog(Desc("r", ToolPermission.ReadOnly)));

        var answer = await routing.RequestAnswerAsync(Req("r", ToolPermission.ReadOnly, "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowBySchedule, answer.Decision);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Outside_the_grant_pauses_waits_for_the_answer_and_then_resumes()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(ScheduleGrant.None);
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce)) { OnRequest = () => host.Events.Add("inner") };
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Single(inner.Requests);
        Assert.Equal(["pause:w", "inner", "resume"], host.Events);   // 暫停在詢問前，恢復在回答之後（Dispose）
        Assert.Empty(host.Grants);
    }

    [Fact]
    public async Task Deny_outside_the_grant_is_returned_and_still_resumes()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(ScheduleGrant.None);
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.Deny));
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(ApprovalDecision.Deny, answer.Decision);
        Assert.Equal(["pause:w", "resume"], host.Events);
    }

    [Fact]
    public async Task Always_allow_on_an_ordinary_tool_writes_the_grant_and_becomes_allow_once()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(ScheduleGrant.None);
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowForConversation));
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);   // 絕不傳回 AllowForConversation
        var grant = Assert.Single(host.Grants);
        Assert.Equal("sched-1", grant.ScheduleId);
        Assert.Equal(ToolPermission.Write, grant.Permission);
    }

    [Theory]
    [InlineData("memory_save", ToolPermission.Write, ToolGroups.Memory, true)]
    [InlineData("cancel_reminder", ToolPermission.Write, ToolGroups.Reminders, false)]
    public async Task Excluded_tools_never_write_a_grant(string name, ToolPermission permission, string group, bool requiresMemory)
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(new ScheduleGrant(true, true, true));
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowForConversation));
        var routing = Routing(inner, host, Catalog(Desc(name, permission, group, requiresMemory)));

        var answer = await routing.RequestAnswerAsync(Req(name, permission, "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Empty(host.Grants);
        Assert.Single(inner.Requests);   // 排除類即使授權涵蓋也要問
    }

    [Fact]
    public async Task Url_warning_asks_even_when_network_is_granted()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(new ScheduleGrant(false, true, false));
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce));
        var routing = Routing(inner, host, Catalog(Desc("net", ToolPermission.Network)));

        var answer = await routing.RequestAnswerAsync(Req("net", ToolPermission.Network, "chat-1", "沒出現過的網站"), default);

        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Equal(["pause:net", "resume"], host.Events);
    }

    [Fact]
    public async Task Tool_missing_from_the_catalog_asks()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(WriteOnly);
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.Deny));
        var routing = Routing(inner, host, Catalog());

        var answer = await routing.RequestAnswerAsync(Req("ghost", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(ApprovalDecision.Deny, answer.Decision);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Amended_arguments_are_returned_unchanged()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(ScheduleGrant.None);
        const string amended = """{"text":"改","granted_permissions":"write"}""";
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce, amended));
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(amended, answer.AmendedArgumentsJson);
    }

    [Fact]
    public async Task Amended_arguments_survive_the_always_allow_downgrade()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(ScheduleGrant.None);
        const string amended = """{"text":"改"}""";
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowForConversation, amended));
        var routing = Routing(inner, host, Catalog(Desc("w", ToolPermission.Write)));

        var answer = await routing.RequestAnswerAsync(Req("w", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Equal(amended, answer.AmendedArgumentsJson);
    }

    [Fact]
    public async Task RequestAsync_returns_the_decision_of_the_answer()
    {
        var host = new FakeScheduledRunHost();
        host.Runs["chat-1"] = Run(WriteOnly);
        var routing = Routing(new AnsweringApprovalService(), host, Catalog(Desc("w", ToolPermission.Write)));

        var decision = await routing.RequestAsync(Req("w", ToolPermission.Write, "chat-1"), default);

        Assert.Equal(ApprovalDecision.AllowBySchedule, decision);
    }
}
