using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests;

public class ToolSnapshotFreezeTests
{
    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Use(string id, string name = "echo") => new(id, name, """{"text":"嗨"}""");

    private static FakeModelCatalog WithTools(ToolSettings tools) =>
        new() { Current = new FakeModelCatalog().Current with { Tools = tools } };

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public Conversation Conversation { get; private set; } = null!;
        public ConversationService Service { get; private set; } = null!;

        public static async Task<Rig> CreateAsync(IChatProvider provider, IEnumerable<ITool> tools, FakeModelCatalog? catalog = null)
        {
            var rig = new Rig();
            rig.Conversation = await rig.Store.CreateAsync("新對話");
            rig.Service = new ConversationService(provider, rig.Store, catalog ?? new FakeModelCatalog(), new ToolRegistry(tools),
                new ScriptedApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(),
                TimeProvider.System, NullLogger<ConversationService>.Instance);
            return rig;
        }
    }

    [Fact]
    public async Task First_send_stores_enabled_tools()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("echo"), new FakeTool("other")],
            WithTools(new ToolSettings { Disabled = ["other"] }));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Equal(["echo"], provider.Requests[0].Tools!.Select(t => t.Name));
        var saved = ToolSnapshotJson.Deserialize(await rig.Store.GetToolSnapshotAsync(rig.Conversation.Id));
        Assert.Equal(["echo"], saved!.Select(t => t.Name));
    }

    [Fact]
    public async Task Later_sends_reuse_snapshot_even_after_settings_change()
    {
        var catalog = WithTools(ToolSettings.Default);
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("echo"), new FakeTool("other")], catalog);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));
        var first = ToolSnapshotJson.Serialize(provider.Requests[0].Tools!);

        catalog.Current = catalog.Current with { Tools = new ToolSettings { Disabled = ["other"] } };
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "再問一次"));

        Assert.Equal(first, ToolSnapshotJson.Serialize(provider.Requests[1].Tools!));
        Assert.Equal(["echo", "other"], provider.Requests[1].Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Legacy_conversation_with_assistant_message_snapshots_all_tools()
    {
        // 舊對話：已經有過助理訊息，但還沒有快照。過去送的是全部工具，所以補上全部（M15 D3）
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("echo"), new FakeTool("other")],
            WithTools(new ToolSettings { Disabled = ["other"] }));
        await rig.Store.AppendMessageAsync(rig.Conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("以前的問題")], DateTimeOffset.UtcNow));
        await rig.Store.AppendMessageAsync(rig.Conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("以前的回答")], DateTimeOffset.UtcNow));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Equal(["echo", "other"], provider.Requests[0].Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task Disabled_tool_in_snapshot_still_executes()
    {
        var tool = new FakeTool("echo");
        var catalog = WithTools(ToolSettings.Default);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Use("t1")),
            SequenceChatProvider.Answer("完成"));
        var rig = await Rig.CreateAsync(provider, [tool], catalog);
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "第一則"));

        catalog.Current = catalog.Current with { Tools = new ToolSettings { Disabled = ["echo"] } };
        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "第二則"));

        Assert.Single(tool.Calls);
        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.False(done.Result.IsError);
    }

    [Fact]
    public async Task Tool_missing_from_catalog_returns_unavailable_error()
    {
        // 快照裡有 gone，但目前的工具裡已經沒有（例如程式更新後拿掉了）：回錯誤，不執行
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "gone")), SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("echo")]);
        await rig.Store.SetToolSnapshotAsync(rig.Conversation.Id,
            ToolSnapshotJson.Serialize([new ToolDefinition("gone", "舊工具。", new JsonObject { ["type"] = "object" })]));

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.True(done.Result.IsError);
        Assert.Equal("這個工具目前無法使用。", done.Result.Content);
        Assert.Equal("無法使用", done.Result.Summary);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Tool_not_in_snapshot_returns_unknown_tool()
    {
        // 工具存在，但這則對話的快照沒有它：照舊回「沒有這個工具」，不執行
        var other = new FakeTool("other");
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "other")), SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("echo"), other]);
        await rig.Store.SetToolSnapshotAsync(rig.Conversation.Id, ToolSnapshotJson.Serialize([ToolDefinition.From(new FakeTool("echo"))]));

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Empty(other.Calls);
        var done = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.True(done.Result.IsError);
        Assert.Equal("沒有這個工具：other", done.Result.Content);
    }

    [Fact]
    public async Task Model_without_tools_sends_no_tools_and_stores_no_snapshot()
    {
        var catalog = new FakeModelCatalog();
        catalog.Current = catalog.Current with { Models = [FakeModelCatalog.Model with { ToolsEnabled = false }] };
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, [new FakeTool("echo")], catalog);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Null(Assert.Single(provider.Requests).Tools);
        Assert.Null(await rig.Store.GetToolSnapshotAsync(rig.Conversation.Id));
    }
}
