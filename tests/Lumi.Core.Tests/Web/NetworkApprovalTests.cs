using System.ComponentModel;
using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Web;

public class NetworkApprovalTests
{
    private sealed record UrlArgs([property: Description("網址")] string Url);

    /// <summary>會連網的測試工具：<c>Preview</c> 帶出要連的網址。</summary>
    private sealed class NetworkTool(ToolPermission permission = ToolPermission.Network) : ToolBase<UrlArgs>
    {
        public override ToolDescriptor Descriptor { get; } =
            new("net", "測試連線", "測試用。", permission, ToolModes.Both, TimeSpan.FromSeconds(5), Group: ToolGroups.Web);

        public List<string> Urls { get; } = [];

        protected override Task<ToolResult> InvokeAsync(UrlArgs args, ToolContext context, CancellationToken ct)
        {
            Urls.Add(args.Url);
            return Task.FromResult(ToolResult.Ok("完成"));
        }

        protected override ToolPreview Describe(UrlArgs args, ToolContext context) => new(args.Url, null, new Uri(args.Url));
    }

    /// <summary>沒有網址的 Network 工具（行為要和舊版一樣）。</summary>
    private sealed class PlainNetworkTool : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor { get; } =
            new("plain", "一般", "測試用。", ToolPermission.Network, ToolModes.Both, TimeSpan.FromSeconds(5));

        public int Runs { get; private set; }

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(ToolResult.Ok("完成"));
        }
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolUseBlock Net(string id, string url) => new(id, "net", $$"""{"url":"{{url}}"}""");

    private static FakeModelCatalog Catalog(params string[] alwaysAllow) =>
        new() { Current = new FakeModelCatalog().Current with { Tools = new ToolSettings { AlwaysAllow = alwaysAllow } } };

    private static ConversationService Service(IChatProvider provider, IEnumerable<ITool> tools, IToolApprovalService approvals,
        FakeModelCatalog catalog, out InMemoryConversationStore store)
    {
        store = new InMemoryConversationStore();
        return new ConversationService(provider, store, catalog, new ToolRegistry(tools), approvals,
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance);
    }

    [Fact]
    public async Task Unknown_site_asks_even_when_always_allowed()
    {
        var tool = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Net("t1", "https://unknown.example/page")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, Catalog("net"), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(approvals.Requests);
        Assert.Single(events.OfType<ApprovalRequired>());
        var warning = Assert.Single(request.Warnings);
        Assert.Contains("unknown.example", warning);
        Assert.True(request.AllowRemember);
        Assert.Equal(new Uri("https://unknown.example/page"), request.Preview.Url);
        Assert.Single(tool.Urls);
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Unknown_site_denied_does_not_run()
    {
        var tool = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.Deny);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Net("t1", "https://unknown.example/page")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, Catalog("net"), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Empty(tool.Urls);
        Assert.Equal(ToolApproval.Denied, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Site_from_user_text_uses_normal_rules()
    {
        var tool = new NetworkTool();
        var approvals = new ScriptedApprovalService();
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Net("t1", "https://a.example/y")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, Catalog("net"), out var store);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "請看 https://a.example/x"));

        Assert.Empty(approvals.Requests);
        Assert.Empty(events.OfType<ApprovalRequired>());
        Assert.Single(tool.Urls);
        Assert.Equal(ToolApproval.AllowedAlways, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Site_from_user_text_still_asks_when_not_always_allowed()
    {
        var tool = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Net("t1", "https://a.example/y")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [tool], approvals, Catalog(), out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "請看 https://a.example/x"));

        var request = Assert.Single(approvals.Requests);
        Assert.Empty(request.Warnings);   // 一般的 Network 確認，沒有網址警告
        Assert.True(request.AllowRemember);
    }

    private static ConversationService ServiceWithLinkSource(string linkText, NetworkTool net, ScriptedApprovalService approvals,
        string secondUrl, FakeModelCatalog catalog, out InMemoryConversationStore store, bool sameIteration)
    {
        var source = new FakeTool("echo") { Behavior = (_, _) => Task.FromResult(ToolResult.Ok(linkText)) };
        var echo = new ToolUseBlock("t1", "echo", """{"text":"x"}""");
        var provider = sameIteration
            ? new SequenceChatProvider(SequenceChatProvider.ToolCalls(echo, Net("t2", secondUrl)), SequenceChatProvider.Answer("好"))
            : new SequenceChatProvider(
                SequenceChatProvider.ToolCalls(echo),
                SequenceChatProvider.ToolCalls(Net("t2", secondUrl)),
                SequenceChatProvider.Answer("好"));
        return Service(provider, [source, net], approvals, catalog, out store);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exact_url_from_tool_result_is_known(bool sameIteration)
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService();
        var service = ServiceWithLinkSource("下一頁：https://next.example/p?id=1", net, approvals, "https://next.example/p?id=1",
            Catalog("net"), out var store, sameIteration);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Empty(approvals.Requests);
        Assert.Single(net.Urls);
        Assert.DoesNotContain(events.OfType<ApprovalRequired>(), _ => true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Different_query_from_tool_result_asks(bool sameIteration)
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.Deny);
        var service = ServiceWithLinkSource("下一頁：https://next.example/p?id=1", net, approvals, "https://next.example/p?id=1&secret=abc",
            Catalog("net"), out var store, sameIteration);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(approvals.Requests);
        Assert.Single(request.Warnings);
        Assert.Empty(net.Urls);
    }

    [Fact]
    public async Task Approved_unknown_site_is_remembered()
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Net("t1", "https://later.example/a")), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Net("t2", "https://later.example/b")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [net], approvals, Catalog("net"), out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "第一則"));
        Assert.Single(approvals.Requests);

        var events = await Collect(service.SendAsync(conversation.Id, "第二則"));

        Assert.Single(approvals.Requests);   // 同網站的其他路徑不再因網址而詢問
        Assert.Equal(2, net.Urls.Count);
        Assert.Equal(ToolApproval.AllowedAlways, Assert.Single(events.OfType<ToolCallCompleted>()).Result.Approval);
    }

    [Fact]
    public async Task Approved_unknown_site_for_conversation_is_remembered_and_other_sites_still_ask()
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowForConversation, ApprovalDecision.Deny);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Net("t1", "https://later.example/a")), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Net("t2", "https://later.example/b"), Net("t3", "https://other.example/")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [net], approvals, Catalog(), out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "第一則"));
        await Collect(service.SendAsync(conversation.Id, "第二則"));

        Assert.Equal(2, approvals.Requests.Count);   // 第一次 later.example；第二則只有 other.example 再問
        Assert.Equal(new Uri("https://other.example/"), approvals.Requests[1].Preview.Url);
        Assert.Equal(["https://later.example/a", "https://later.example/b"], net.Urls);
    }

    [Fact]
    public async Task Approved_site_is_per_conversation()
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce, ApprovalDecision.Deny);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Net("t1", "https://later.example/a")), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Net("t2", "https://later.example/b")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [net], approvals, Catalog("net"), out var store);
        var first = await store.CreateAsync("一");
        var second = await store.CreateAsync("二");

        await Collect(service.SendAsync(first.Id, "嗨"));
        await Collect(service.SendAsync(second.Id, "嗨"));

        Assert.Equal(2, approvals.Requests.Count);
    }

    [Fact]
    public async Task Denied_unknown_site_is_not_remembered()
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.Deny, ApprovalDecision.Deny);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Net("t1", "https://later.example/a")), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Net("t2", "https://later.example/b")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [net], approvals, Catalog("net"), out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "第一則"));
        await Collect(service.SendAsync(conversation.Id, "第二則"));

        Assert.Equal(2, approvals.Requests.Count);
    }

    [Fact]
    public async Task Search_engine_always_asks_and_cannot_be_remembered()
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowForConversation, ApprovalDecision.AllowOnce);
        var search = "https://www.google.com/search?q=lumi";
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Net("t1", search)), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Net("t2", search)), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [net], approvals, Catalog("net"), out var store);
        var conversation = await store.CreateAsync("測試");

        // 使用者自己貼了搜尋網址，仍然要問
        var first = await Collect(service.SendAsync(conversation.Id, $"幫我查 {search}"));

        var request = Assert.Single(approvals.Requests);
        Assert.False(request.AllowRemember);
        Assert.Contains("搜尋引擎", Assert.Single(request.Warnings));
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(first.OfType<ToolCallCompleted>()).Result.Approval);   // AllowForConversation 當成允許一次

        var second = await Collect(service.SendAsync(conversation.Id, "再查一次"));

        Assert.Equal(2, approvals.Requests.Count);   // 下一次仍然詢問
        Assert.Equal(ToolApproval.AllowedOnce, Assert.Single(second.OfType<ToolCallCompleted>()).Result.Approval);
        Assert.Equal(2, net.Urls.Count);
    }

    [Fact]
    public async Task Search_engine_allow_does_not_remember_tool_or_site()
    {
        var net = new NetworkTool();
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowForConversation, ApprovalDecision.Deny);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(Net("t1", "https://www.google.com/search?q=x")), SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(Net("t2", "https://other.example/")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [net], approvals, Catalog(), out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "第一則"));
        await Collect(service.SendAsync(conversation.Id, "第二則"));

        // 「本次對話都允許」沒有被記成 Network 等級，所以第二個網站（沒出現過）照樣問
        Assert.Equal(2, approvals.Requests.Count);
        Assert.DoesNotContain(net.Urls, u => u.Contains("other.example"));
    }

    [Fact]
    public async Task Tools_without_url_are_unchanged()
    {
        var plain = new PlainNetworkTool();
        var readOnly = new FakeTool("echo");
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowForConversation);
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "echo", """{"text":"x"}"""), new ToolUseBlock("t2", "plain", """{"text":"x"}""")),
            SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t3", "plain", """{"text":"x"}""")),
            SequenceChatProvider.Answer("好"));
        var service = Service(provider, [plain, readOnly], approvals, Catalog(), out var store);
        var conversation = await store.CreateAsync("測試");

        var first = await Collect(service.SendAsync(conversation.Id, "第一則"));

        var request = Assert.Single(approvals.Requests);   // 唯讀工具不問；Network 工具照舊問一次
        Assert.Equal("plain", request.ToolName);
        Assert.Empty(request.Warnings);
        Assert.True(request.AllowRemember);
        Assert.Contains(first.OfType<ToolCallCompleted>(), c => c.Result.Approval == ToolApproval.NotRequired);

        var second = await Collect(service.SendAsync(conversation.Id, "第二則"));

        Assert.Single(approvals.Requests);   // 「本次對話都允許」照舊生效
        Assert.Equal(ToolApproval.AllowedForConversation, Assert.Single(second.OfType<ToolCallCompleted>()).Result.Approval);
        Assert.Equal(2, plain.Runs);
    }

    [Fact]
    public async Task Read_only_tool_with_unknown_url_asks()
    {
        var net = new NetworkTool(ToolPermission.ReadOnly);
        var approvals = new ScriptedApprovalService(ApprovalDecision.AllowOnce);
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Net("t1", "https://unknown.example/")), SequenceChatProvider.Answer("好"));
        var service = Service(provider, [net], approvals, Catalog(), out var store);
        var conversation = await store.CreateAsync("測試");

        await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Single(approvals.Requests);
        Assert.Single(net.Urls);
    }

    [Fact]
    public void Permission_memory_sites_are_per_conversation_and_cleared()
    {
        var memory = new ToolPermissionMemory();

        memory.ApproveSite("c1", "https://a.example:443");

        Assert.True(memory.IsSiteApproved("c1", "https://a.example:443"));
        Assert.False(memory.IsSiteApproved("c2", "https://a.example:443"));
        Assert.False(memory.IsSiteApproved("c1", "https://b.example:443"));

        memory.Clear("c1");

        Assert.False(memory.IsSiteApproved("c1", "https://a.example:443"));
    }
}
