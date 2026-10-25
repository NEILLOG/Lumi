using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests;

public class AlwaysAllowTests
{
    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Use(string id, string name) => new(id, name, """{"text":"嗨"}""");

    private static FakeModelCatalog WithTools(ToolSettings tools) =>
        new() { Current = new FakeModelCatalog().Current with { Tools = tools } };

    private static ToolDescriptor Desc(string name, ToolPermission permission, bool canAlwaysAllow = true) =>
        new(name, "測試動作", "測試用說明。", permission, ToolModes.Both, TimeSpan.FromSeconds(5), CanAlwaysAllow: canAlwaysAllow);

    /// <summary>記錄執行次數的工具，可以指定權限與能否設「永遠允許」。</summary>
    private sealed class CountingTool(ToolDescriptor descriptor) : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor => descriptor;

        public int Runs { get; private set; }

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(ToolResult.Ok("完成"));
        }
    }

    private static ConversationService Service(IChatProvider provider, IEnumerable<ITool> tools, IToolApprovalService approvals,
        FakeModelCatalog catalog, out InMemoryConversationStore store)
    {
        store = new InMemoryConversationStore();
        return new ConversationService(provider, store, catalog, new ToolRegistry(tools), approvals,
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance);
    }

    [Fact]
    public async Task Always_allowed_write_tool_runs_without_asking()
    {
        var tool = new CountingTool(Desc("w", ToolPermission.Write));
        var approvals = new ScriptedApprovalService();
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "w")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, WithTools(new ToolSettings { AlwaysAllow = ["w"] }), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Empty(approvals.Requests);
        Assert.Empty(events.OfType<ApprovalRequired>());
        Assert.Equal(1, tool.Runs);
        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.False(done.Result.IsError);
        Assert.Equal(ToolApproval.AllowedAlways, done.Result.Approval);
    }

    [Fact]
    public async Task Destructive_tool_in_always_allow_still_asks()
    {
        var tool = new CountingTool(Desc("d", ToolPermission.Destructive));
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "d")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, WithTools(new ToolSettings { AlwaysAllow = ["d"] }), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Single(approvals.Requests);
        Assert.Single(events.OfType<ApprovalRequired>());
        Assert.Equal(1, tool.Runs);
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Memory_save_in_always_allow_still_asks()
    {
        var tool = new CountingTool(Desc("memory_save", ToolPermission.Write, canAlwaysAllow: false));
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "memory_save")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, WithTools(new ToolSettings { AlwaysAllow = ["memory_save"] }), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Single(approvals.Requests);
        Assert.Single(events.OfType<ApprovalRequired>());
        Assert.Equal(1, tool.Runs);
    }

    [Fact]
    public async Task Always_allow_applies_immediately_in_existing_conversation()
    {
        var tool = new CountingTool(Desc("w", ToolPermission.Write));
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce, ApprovalDecision.AllowOnce);
        var catalog = WithTools(ToolSettings.Default);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("t1", "w")), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Use("t2", "w")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, catalog, out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "第一則"));
        Assert.Single(approvals.Requests);

        catalog.Current = catalog.Current with { Tools = new ToolSettings { AlwaysAllow = ["w"] } };
        var events = await Collect(service.SendAsync(conversation.Id, "第二則"));

        Assert.Single(approvals.Requests);   // 第二則沒有再詢問
        Assert.Equal(2, tool.Runs);
        Assert.Equal(ToolApproval.AllowedAlways, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }
}
