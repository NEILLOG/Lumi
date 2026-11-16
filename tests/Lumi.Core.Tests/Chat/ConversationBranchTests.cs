using System.Text.RegularExpressions;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>M36 包 36E：支線的請求組法（前綴與上一層逐則相同、出處與摘要區塊、整理、用量、擋下的情況）。</summary>
public class ConversationBranchTests
{
    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new ModeFormatPromptLayer(), new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
    ]);

    /// <summary>記下每一次用量觀察的假紀錄器。</summary>
    private sealed class CapturingUsage : IUsageRecorder
    {
        public List<UsageObservation> Records { get; } = [];

        public Task RecordAsync(UsageObservation observation, CancellationToken ct = default)
        {
            Records.Add(observation);
            return Task.CompletedTask;
        }
    }

    /// <summary>摘要的假實作：不呼叫模型，固定回一段文字。</summary>
    private sealed class FakeSummarizer : IBranchSummarizer
    {
        public Task<BranchSummaryDraft> SummarizeAsync(BranchSummaryRequest request, CancellationToken ct = default) =>
            Task.FromResult(new BranchSummaryDraft("摘要", null));
    }

    /// <summary>一個網頁工具（唯讀）：預覽帶著網址，用來測「主線給的網址在支線裡也算看過」。</summary>
    private sealed class WebTool(ToolPreview preview) : ToolBase<EchoArgs>
    {
        public override ToolDescriptor Descriptor { get; } = new("web_fetch", "網頁", "讀網頁。測試用。", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5));

        protected override ToolPreview Describe(EchoArgs args, ToolContext context) => preview;

        protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("網頁內容"));
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ConversationService Service(
        IChatProvider provider, InMemoryConversationStore store, InMemoryBranchStore? branches = null, FakeModelCatalog? catalog = null,
        IEnumerable<ITool>? tools = null, ScriptedApprovalService? approvals = null, IUsageRecorder? usage = null) =>
        new(provider, store, catalog ?? new FakeModelCatalog(), new ToolRegistry(tools ?? []), approvals ?? new ScriptedApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            composer: Composer, branches: branches, usageRecorder: usage);

    private static BranchService Branches(InMemoryConversationStore store, InMemoryBranchStore branches) =>
        new(store, branches, new InMemoryConversationContextStore(), new FakeSummarizer(), new SpriteConversationService(store), TimeProvider.System,
            NullLogger<BranchService>.Instance);

    private static string Text(ChatMessage message) => string.Concat(message.Content.OfType<TextBlock>().Select(t => t.Text));

    /// <summary>比對用的文字：角色、後綴、內容（不含 id，id 每次都不同）。</summary>
    private static string Projection(ChatMessage message) =>
        $"{message.Role}|{message.TurnSuffix}|{string.Join("§", message.Content.Select(c => c.ToString()))}";

    /// <summary>比對用的文字：含 id，同一則訊息在上一層與支線的請求裡要是同一個 id。</summary>
    private static string Identity(ChatMessage message) => $"{message.Id}|{Projection(message)}";

    private static string ToolsJson(IReadOnlyList<ToolDefinition>? tools) => tools is null ? "" : ToolSnapshotJson.Serialize(tools);

    private static async Task<string> LastAssistantIdAsync(InMemoryConversationStore store, string conversationId) =>
        (await store.GetMessagesAsync(conversationId)).Last(m => m.Role == ChatRole.Assistant).Id;

    private static async Task<string> AddUserAsync(InMemoryConversationStore store, string conversationId, string text)
    {
        var message = new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);
        await store.AppendMessageAsync(conversationId, message);
        return message.Id;
    }

    private static async Task<string> AddAssistantAsync(InMemoryConversationStore store, string conversationId, string text)
    {
        var message = new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow);
        await store.AppendMessageAsync(conversationId, message);
        return message.Id;
    }

    [Fact]
    public async Task Branch_first_request_prefix_equals_parent_last_request()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("主線的回答"), SequenceChatProvider.Answer("支線的回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "第一個問題"));

        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), "回答");
        await Collect(service.SendAsync(child.Id, "這段是什麼意思"));

        var parent = provider.Requests[0];
        var request = provider.Requests[1];
        Assert.NotNull(parent.System);
        Assert.Equal(parent.System, request.System);
        Assert.Equal(ToolsJson(parent.Tools), ToolsJson(request.Tools));
        // 上一層最後一次請求的每一則（含 TurnSuffix），在支線的請求裡逐則相同
        Assert.Equal(parent.Messages.Select(Identity).ToList(), request.Messages.Take(parent.Messages.Count).Select(Identity).ToList());
        // 接著是上一層的回答，最後是支線的第一則
        Assert.Equal(ChatRole.Assistant, request.Messages[parent.Messages.Count].Role);
        Assert.Equal("主線的回答", Text(request.Messages[parent.Messages.Count]));
        Assert.Equal(parent.Messages.Count + 2, request.Messages.Count);
    }

    [Fact]
    public async Task Branch_first_message_has_origin_and_no_digest()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        // 另一則舊對話：近況清單會列到它
        var old = await store.CreateAsync("舊對話");
        await AddUserAsync(store, old.Id, "以前的問題");
        await AddAssistantAsync(store, old.Id, "以前的回答");

        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("主線回答"), SequenceChatProvider.Answer("支線回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "新問題"));
        // 主線的第一則有近況清單（確認這個情境下近況清單確實會出現）
        Assert.Contains(provider.Requests[0].Messages[0].Content, c => c is ContextBlock { Kind: ContextKind.RecentConversations });

        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), "新問題");
        await Collect(service.SendAsync(child.Id, "問這段"));

        var first = provider.Requests[1].Messages[^1];
        Assert.Contains(first.Content, c => c is ContextBlock { Kind: ContextKind.BranchOrigin });
        Assert.DoesNotContain(first.Content, c => c is ContextBlock { Kind: ContextKind.RecentConversations });
    }

    [Fact]
    public async Task Second_level_prefix_contains_root_and_first_level()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("主線回答"), SequenceChatProvider.Answer("第一層回答"), SequenceChatProvider.Answer("第二層回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "第一個問題"));

        var branchService = Branches(store, branches);
        var first = await branchService.StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);
        await Collect(service.SendAsync(first.Id, "第一層的問題"));
        var second = await branchService.StartAsync(first.Id, await LastAssistantIdAsync(store, first.Id), null);
        await Collect(service.SendAsync(second.Id, "第二層的問題"));

        var firstRequest = provider.Requests[1];
        var secondRequest = provider.Requests[2];
        // 第二層的前綴 = 第一層最後一次請求（主線＋第一層的第一則）逐則相同
        Assert.Equal(firstRequest.Messages.Select(Identity).ToList(), secondRequest.Messages.Take(firstRequest.Messages.Count).Select(Identity).ToList());
        // 主線 2 則（問題、回答）＋第一層 2 則（問題、回答）＋第二層的問題
        Assert.Equal(
            new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User },
            secondRequest.Messages.Select(m => m.Role).ToArray());
        Assert.Contains(secondRequest.Messages[2].Content, c => c is ContextBlock { Kind: ContextKind.BranchOrigin });
    }

    [Fact]
    public async Task Root_compaction_is_applied_to_prefix()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("第一回答"), SequenceChatProvider.Answer("第二回答"), SequenceChatProvider.Answer("支線回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "第一句"));
        await Collect(service.SendAsync(main.Id, "第二句"));

        // 摘要涵蓋第一輪（到第一則回答為止，順序號 2）
        var firstAnswer = (await store.GetMessagesAsync(main.Id))[1];
        await store.AddCompactionAsync(main.Id,
            new CompactionRecord(CompactionRecord.NewId(), CompactionKind.Summary, firstAnswer.Seq, "摘要內容：第一輪的決定", "test-model", DateTimeOffset.UtcNow));

        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);
        await Collect(service.SendAsync(child.Id, "問這段"));

        var request = provider.Requests[2];
        var beginning = Text(request.Messages[0]);
        Assert.Contains("摘要內容：第一輪的決定", beginning);
        Assert.Contains("第二句", beginning);
        Assert.DoesNotContain("第一句", beginning);
    }

    [Fact]
    public async Task Undelivered_summary_is_attached_once_and_marked()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("回答一"), SequenceChatProvider.Answer("支線回答"),
            SequenceChatProvider.Answer("回答二"), SequenceChatProvider.Answer("回答三"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "嗨"));

        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), "回答");
        await Collect(service.SendAsync(child.Id, "問一下"));
        await Branches(store, branches).MergeAsync(child.Id, "帶回的摘要");

        await Collect(service.SendAsync(main.Id, "第二則"));
        await Collect(service.SendAsync(main.Id, "第三則"));

        // 第二則帶著摘要（區塊在使用者訊息裡）；第三則沒有
        var second = provider.Requests[2].Messages[^1];
        var summary = Assert.Single(second.Content.OfType<ContextBlock>(), b => b.Kind == ContextKind.BranchSummary);
        Assert.Equal("帶回的摘要", summary.Content);
        var third = provider.Requests[3].Messages[^1];
        Assert.DoesNotContain(third.Content, c => c is ContextBlock { Kind: ContextKind.BranchSummary });

        // 記下交給了第二則那則
        var secondStored = (await store.GetMessagesAsync(main.Id)).Single(m => m.Role == ChatRole.User && Text(m) == "第二則");
        Assert.Equal(secondStored.Id, (await branches.GetAsync(child.Id))!.DeliveredMessageId);
    }

    [Fact]
    public async Task Send_with_active_child_throws()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "嗨"));
        await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(service.SendAsync(main.Id, "再問")));
        Assert.Equal(BranchMessages.ActiveChildMessage, ex.Message);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Retry_and_regenerate_with_active_child_throw()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "嗨"));
        await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);

        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(service.RetryAsync(main.Id)));
        var regenerate = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(service.RegenerateAsync(main.Id)));
        Assert.Equal(BranchMessages.ActiveChildMessage, retry.Message);
        Assert.Equal(BranchMessages.ActiveChildMessage, regenerate.Message);
        // 上一層的回答沒有被移除
        Assert.Equal(2, (await store.GetMessagesAsync(main.Id)).Count);
    }

    [Fact]
    public async Task Send_in_finished_branch_throws()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("主線回答"), SequenceChatProvider.Answer("支線回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "嗨"));
        var branchService = Branches(store, branches);
        var child = await branchService.StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);
        await Collect(service.SendAsync(child.Id, "問"));
        await branchService.MergeAsync(child.Id, "摘要");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(service.SendAsync(child.Id, "又問")));
        Assert.Equal(BranchMessages.FinishedBranchMessage, ex.Message);
    }

    [Fact]
    public async Task Context_too_long_in_branch_fails_without_compaction()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("主線回答"),
            [new Failed(new ChatFailure(ChatFailureKind.ContextTooLong, "超過上限", Retryable: false))]);
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "嗨"));
        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);

        var events = await Collect(service.SendAsync(child.Id, "問"));

        var failed = Assert.Single(events.OfType<Failed>());
        Assert.Equal(BranchMessages.BranchTooLongMessage, failed.Failure.Message);
        Assert.Empty(events.OfType<CompactionStarted>());
        // 只呼叫了一次（沒有整理後重送）
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Compact_in_branch_reports_failure()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("主線回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "嗨"));
        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);

        var events = await Collect(service.CompactAsync(child.Id));

        var failure = Assert.Single(events.OfType<CompactionFailed>());
        Assert.Equal(BranchMessages.BranchCompactMessage, failure.Failure.Message);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Compact_with_active_child_reports_failure()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("主線回答"));
        var service = Service(provider, store, branches);
        await Collect(service.SendAsync(main.Id, "嗨"));
        await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);

        var events = await Collect(service.CompactAsync(main.Id));

        var failure = Assert.Single(events.OfType<CompactionFailed>());
        Assert.Equal(BranchMessages.ActiveChildMessage, failure.Failure.Message);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task Branch_usage_is_recorded_on_root_with_branch_purpose()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var usage = new CapturingUsage();
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("主線回答"), SequenceChatProvider.Answer("支線回答"));
        var service = Service(provider, store, branches, usage: usage);
        await Collect(service.SendAsync(main.Id, "嗨"));
        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);
        await Collect(service.SendAsync(child.Id, "問"));

        var mainCall = Assert.Single(usage.Records, r => r.Purpose == UsagePurpose.Chat);
        Assert.Equal(main.Id, mainCall.ConversationId);
        var branchCall = Assert.Single(usage.Records, r => r.Purpose == UsagePurpose.Branch);
        Assert.Equal(main.Id, branchCall.ConversationId);
    }

    [Fact]
    public async Task Main_user_url_is_known_in_branch()
    {
        var store = new InMemoryConversationStore();
        var branches = new InMemoryBranchStore();
        var approvals = new ScriptedApprovalService();
        var web = new WebTool(new ToolPreview("https://a.example/y", null, Url: new Uri("https://a.example/y")));
        var main = await store.CreateAsync("主線");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("好"),
            SequenceChatProvider.ToolCalls(new ToolUseBlock("u1", "web_fetch", """{"text":"嗨"}""")),
            SequenceChatProvider.Answer("讀完了"));
        var service = Service(provider, store, branches, tools: [web], approvals: approvals);
        await Collect(service.SendAsync(main.Id, "請看 https://a.example/x 這個網站"));
        var child = await Branches(store, branches).StartAsync(main.Id, await LastAssistantIdAsync(store, main.Id), null);

        var events = await Collect(service.SendAsync(child.Id, "幫我讀 https://a.example/y"));

        // 同一個網站、使用者在主線給過網址：不詢問，直接執行
        Assert.Empty(approvals.Requests);
        Assert.Contains(events, e => e is ToolCallCompleted);
    }

    [Fact]
    public async Task Non_branch_requests_are_unchanged()
    {
        // 沒有支線的對話：注入支線儲存（裡面有別的對話的支線）與不注入，送出的請求逐則相同
        async Task<List<ChatRequest>> RunAsync(bool withOtherBranch)
        {
            var store = new InMemoryConversationStore();
            var branches = new InMemoryBranchStore();
            var other = await store.CreateAsync("別的主線");
            var otherAnswer = await AddAssistantAsync(store, other.Id, "別的回答");
            if (withOtherBranch)
                await Branches(store, branches).StartAsync(other.Id, otherAnswer, null);

            var conversation = await store.CreateAsync("新對話");
            var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("再見"));
            var service = Service(provider, store, branches);
            await Collect(service.SendAsync(conversation.Id, "嗨"));
            await Collect(service.SendAsync(conversation.Id, "再來"));
            return provider.Requests;
        }

        var plain = await RunAsync(withOtherBranch: false);
        var withBranches = await RunAsync(withOtherBranch: true);

        // 近況清單會列出別的對話，它的 id 每次執行都不同，比對前拿掉
        static string Normalize(string text) => Regex.Replace(text, "id=[0-9a-zA-Z-]+", "id=");
        Assert.Equal(
            plain.Select(r => Normalize(string.Join("\n", r.Messages.Select(Projection)))).ToList(),
            withBranches.Select(r => Normalize(string.Join("\n", r.Messages.Select(Projection)))).ToList());
        Assert.Equal(plain.Select(r => (r.System, ToolsJson(r.Tools), r.TurnSuffix)).ToList(), withBranches.Select(r => (r.System, ToolsJson(r.Tools), r.TurnSuffix)).ToList());
        Assert.All(plain, r => Assert.DoesNotContain(r.Messages, m => m.Content.OfType<ContextBlock>().Any(b => b.Kind is ContextKind.BranchOrigin or ContextKind.BranchSummary)));
    }
}
