using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Tools;

/// <summary>預設關閉的工具（M31 D13、D14）、拒絕執行（D11）與外部內容警告（D12）在對話迴圈的行為。</summary>
public class OptInToolTests
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

    /// <summary>可以指定描述符與預覽的假工具（需要寫入權限、預設關閉、被擋的情境用）。</summary>
    private sealed class TestTool(ToolDescriptor descriptor, ToolPreview? preview = null) : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor => descriptor;

        public List<EchoArgs> Calls { get; } = [];

        protected override ToolPreview Describe(EchoArgs args, ToolContext context) => preview ?? new ToolPreview(args.Text, null);

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(ToolResult.Ok(args.Text));
        }
    }

    private static ToolDescriptor OptInDescriptor(string name = "opt") =>
        new(name, "預設關閉", "測試用，預設關閉的寫入工具。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5),
            OptInWarning: "開啟後，模型可以寫入檔案。");

    private static ToolDescriptor WriteDescriptor(string name, bool warnsAfterExternal = false) =>
        new(name, name, "測試用寫入工具。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5),
            WarnsAfterExternalContent: warnsAfterExternal);

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public Conversation Conversation { get; private set; } = null!;
        public ConversationService Service { get; private set; } = null!;
        public ScriptedApprovalService Approvals { get; } = new(ApprovalDecision.AllowOnce, ApprovalDecision.AllowOnce, ApprovalDecision.AllowOnce);

        public static async Task<Rig> CreateAsync(IChatProvider provider, IEnumerable<ITool> tools, FakeModelCatalog catalog)
        {
            var rig = new Rig();
            rig.Conversation = await rig.Store.CreateAsync("新對話");
            rig.Service = new ConversationService(provider, rig.Store, catalog, new ToolRegistry(tools),
                rig.Approvals, new ToolPermissionMemory(), new ToolLoopOptions(),
                TimeProvider.System, NullLogger<ConversationService>.Instance);
            return rig;
        }
    }

    [Fact]
    public async Task Old_conversation_backfill_excludes_opt_in_tools()
    {
        // 舊對話：已有助理訊息、還沒有快照。補「全部」工具時不能把預設關閉的工具補進去（M31 D13）
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("echo"), new TestTool(OptInDescriptor())],
            WithTools(ToolSettings.Default));
        await rig.Store.AppendMessageAsync(rig.Conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("以前的問題")], DateTimeOffset.UtcNow));
        await rig.Store.AppendMessageAsync(rig.Conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("以前的回答")], DateTimeOffset.UtcNow));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Equal(["echo"], provider.Requests[0].Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Turning_off_takes_effect_in_open_conversation()
    {
        // 新對話時開啟，快照裡有它；之後在設定關掉：已經開著的對話呼叫時回錯誤，不詢問、不執行（M31 D14）
        var tool = new TestTool(OptInDescriptor());
        var catalog = WithTools(new ToolSettings { Enabled = ["opt"] });
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Use("t1", "opt")),
            SequenceChatProvider.Answer("完成"));
        var rig = await Rig.CreateAsync(provider, [tool], catalog);
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "第一則"));
        Assert.Equal(["opt"], ToolSnapshotJson.Deserialize(await rig.Store.GetToolSnapshotAsync(rig.Conversation.Id))!.Select(t => t.Name));

        catalog.Current = catalog.Current with { Tools = new ToolSettings() };
        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "第二則"));

        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.True(done.Result.IsError);
        Assert.Equal("這個工具已經在設定關閉，不能使用。", done.Result.Content);
        Assert.Equal("已關閉", done.Result.Summary);
        Assert.Equal(ToolApproval.NotRequired, done.Result.Approval);
        Assert.Empty(events.OfType<ApprovalRequired>());
        Assert.Empty(rig.Approvals.Requests);
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task Rejection_returns_error_without_asking()
    {
        // 工具自己擋掉（例如被擋的指令）：不詢問、不執行，原因直接回給模型（M31 D11）
        const string reason = "這段指令被 Lumi 擋下：測試用原因。";
        var tool = new TestTool(WriteDescriptor("blocked"), new ToolPreview(null, null, Rejection: reason));
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "blocked")), SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [tool], WithTools(ToolSettings.Default));

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.True(done.Result.IsError);
        Assert.Equal(reason, done.Result.Content);
        Assert.Equal("已擋下", done.Result.Summary);
        Assert.Equal(ToolApproval.NotRequired, done.Result.Approval);
        Assert.Empty(events.OfType<ApprovalRequired>());
        Assert.Empty(rig.Approvals.Requests);
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task External_content_adds_warning_for_flagged_tool()
    {
        // 警告只在「這則對話讀過外部內容」之後的確認卡出現，而且放在最後；沒有旗標的工具不加
        var flagged = new TestTool(WriteDescriptor("flagged", warnsAfterExternal: true));
        var plain = new TestTool(WriteDescriptor("plain"));
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Use("f1", "flagged")),   // 還沒讀過外部內容：沒有警告
            SequenceChatProvider.ToolCalls(Use("w1", "web_fetch")),  // 讀網頁（唯讀，不詢問）
            SequenceChatProvider.ToolCalls(Use("f2", "flagged")),   // 讀過外部內容：有警告
            SequenceChatProvider.ToolCalls(Use("p1", "plain")),     // 沒有旗標：沒有警告
            SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [flagged, plain, new FakeTool("web_fetch")], WithTools(ToolSettings.Default));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var requests = rig.Approvals.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Empty(requests[0].Warnings);
        Assert.Equal([ExternalContentProvenance.Warning], requests[1].Warnings);
        Assert.Empty(requests[2].Warnings);
    }
}
