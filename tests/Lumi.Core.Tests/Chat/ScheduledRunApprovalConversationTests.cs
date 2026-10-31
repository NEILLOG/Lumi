using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>對話迴圈的權限段落（M24 4.3、4.4）：排程授權、AlwaysAsk、修改後參數、granted_permissions 防偽造。</summary>
public class ScheduledRunApprovalConversationTests
{
    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Use(string id, string name, string json = """{"text":"嗨"}""") => new(id, name, json);

    private static FakeModelCatalog WithTools(ToolSettings tools) =>
        new() { Current = new FakeModelCatalog().Current with { Tools = tools } };

    private static ConversationService Service(IChatProvider provider, IEnumerable<ITool> tools, IToolApprovalService approvals,
        FakeModelCatalog catalog, ToolPermissionMemory memory, out InMemoryConversationStore store)
    {
        store = new InMemoryConversationStore();
        return new ConversationService(provider, store, catalog, new ToolRegistry(tools), approvals,
            memory, new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance);
    }

    private static ActiveScheduledRun Run(ScheduleGrant grant) => new("run-1", "sched-1", "每天整理", grant);

    private static async Task<ToolUseBlock> StoredToolUse(InMemoryConversationStore store, string conversationId) =>
        (await store.GetMessagesAsync(conversationId)).SelectMany(m => m.Content).OfType<ToolUseBlock>().Single();

    [Fact]
    public async Task Tool_inside_the_schedule_grant_runs_without_asking_and_is_recorded_as_allowed_by_schedule()
    {
        var tool = new FakeTool("w", ToolPermission.Write);
        var catalog = WithTools(ToolSettings.Default);
        var host = new FakeScheduledRunHost();
        var inner = new AnsweringApprovalService();
        var routing = new RoutingToolApprovalService(inner, host, new ToolCatalog(new ToolRegistry([tool]), catalog));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "w")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], routing, catalog, new ToolPermissionMemory(), out var store);
        var conversation = await store.CreateAsync("排程");
        host.Runs[conversation.Id] = Run(new ScheduleGrant(Write: true, Network: false, Destructive: false));

        var events = await Collect(service.SendAsync(conversation.Id, "執行"));

        Assert.Empty(inner.Requests);
        Assert.Empty(events.OfType<ApprovalRequired>());
        Assert.Single(tool.Calls);
        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.False(done.Result.IsError);
        Assert.Equal(ToolApproval.AllowedBySchedule, done.Result.Approval);
    }

    [Fact]
    public async Task Tool_outside_the_grant_pauses_then_runs_after_the_answer()
    {
        var tool = new FakeTool("w", ToolPermission.Write);
        var catalog = WithTools(ToolSettings.Default);
        var host = new FakeScheduledRunHost();
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce)) { OnRequest = () => host.Events.Add("inner") };
        var routing = new RoutingToolApprovalService(inner, host, new ToolCatalog(new ToolRegistry([tool]), catalog));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "w")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], routing, catalog, new ToolPermissionMemory(), out var store);
        var conversation = await store.CreateAsync("排程");
        host.Runs[conversation.Id] = Run(ScheduleGrant.None);

        var events = await Collect(service.SendAsync(conversation.Id, "執行"));

        Assert.Single(inner.Requests);
        Assert.Equal(conversation.Id, inner.Requests[0].ConversationId);
        Assert.Equal(["pause:w", "inner", "resume"], host.Events);
        Assert.Single(tool.Calls);
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Always_ask_tool_is_not_skipped_by_the_conversation_memory()
    {
        var descriptor = new ToolDescriptor("ask", "建立", "測試用。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5), AlwaysAsk: true);
        var tool = new DescriptorTool(descriptor);
        var inner = new AnsweringApprovalService(
            new ApprovalAnswer(ApprovalDecision.AllowForConversation), new ApprovalAnswer(ApprovalDecision.AllowForConversation));
        var memory = new ToolPermissionMemory();
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("t1", "ask")), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Use("t2", "ask")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], inner, WithTools(ToolSettings.Default), memory, out var store);
        var conversation = await store.CreateAsync("測試");

        var first = await Collect(service.SendAsync(conversation.Id, "第一則"));
        var second = await Collect(service.SendAsync(conversation.Id, "第二則"));

        Assert.Equal(2, inner.Requests.Count);   // 第二則沒有被「本次對話都允許」跳過
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(first.OfType<ToolCallCompleted>()).Result.Approval);
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(second.OfType<ToolCallCompleted>()).Result.Approval);
        Assert.Equal(2, tool.Args.Count);
        Assert.False(memory.IsAllowed(conversation.Id, descriptor));   // 什麼都沒記
        Assert.False(inner.Requests[0].AllowRemember);
    }

    [Fact]
    public async Task Always_ask_tool_is_not_skipped_by_the_settings_always_allow()
    {
        var descriptor = new ToolDescriptor("ask", "建立", "測試用。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5), AlwaysAsk: true);
        var tool = new DescriptorTool(descriptor);
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "ask")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], inner, WithTools(new ToolSettings { AlwaysAllow = ["ask"] }), new ToolPermissionMemory(), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Single(inner.Requests);
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Always_ask_tool_in_a_scheduled_run_asks_even_with_full_grant()
    {
        var descriptor = new ToolDescriptor("ask", "建立", "測試用。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5), AlwaysAsk: true);
        var tool = new DescriptorTool(descriptor);
        var catalog = WithTools(ToolSettings.Default);
        var host = new FakeScheduledRunHost();
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce));
        var routing = new RoutingToolApprovalService(inner, host, new ToolCatalog(new ToolRegistry([tool]), catalog));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "ask")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], routing, catalog, new ToolPermissionMemory(), out var store);
        var conversation = await store.CreateAsync("排程");
        host.Runs[conversation.Id] = Run(new ScheduleGrant(true, true, true));

        var events = await Collect(service.SendAsync(conversation.Id, "執行"));

        Assert.Single(inner.Requests);
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Amended_arguments_are_executed_while_the_stored_tool_use_keeps_the_model_arguments()
    {
        var tool = new ScheduleProbeTool();
        const string modelArgs = """{"text":"嗨","granted_permissions":"network"}""";
        const string amended = """{"text":"改過的","granted_permissions":"write"}""";
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce, amended));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "create_probe", modelArgs)), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], inner, WithTools(ToolSettings.Default), new ToolPermissionMemory(), out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "嗨"));

        var ran = Assert.Single(tool.Args);
        Assert.Equal("改過的", ran.Text);
        Assert.Equal("write", ran.GrantedPermissions);
        Assert.Equal(modelArgs, (await StoredToolUse(store, conversation.Id)).InputJson);
    }

    [Fact]
    public async Task Without_amendment_granted_permissions_from_the_model_is_removed_before_running()
    {
        var tool = new ScheduleProbeTool();
        const string modelArgs = """{"text":"嗨","granted_permissions":"destructive"}""";
        var inner = new AnsweringApprovalService(new ApprovalAnswer(ApprovalDecision.AllowOnce));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "create_probe", modelArgs)), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], inner, WithTools(ToolSettings.Default), new ToolPermissionMemory(), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        var ran = Assert.Single(tool.Args);
        Assert.Equal("嗨", ran.Text);
        Assert.Null(ran.GrantedPermissions);   // 模型自己填的授權不可信
        Assert.Equal(modelArgs, (await StoredToolUse(store, conversation.Id)).InputJson);
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }
}
