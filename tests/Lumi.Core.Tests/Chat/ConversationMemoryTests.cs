using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>長期記憶與對話迴圈的接線（M14 的 14B）：每一輪讀取有效記憶、記憶關閉時不送、記憶工具依設定過濾。</summary>
public class ConversationMemoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new MemoryPromptLayer(), new ModeFormatPromptLayer(),
        new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
    ]);

    private static MemoryItem Item(int id, string content) =>
        new(id, content, MemoryCategory.Profile, MemorySource.Explicit, null, Now, Now, null);

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ConversationService Service(IChatProvider provider, InMemoryConversationStore store, FakeModelCatalog catalog,
        IMemoryStore? memory, IEnumerable<ITool>? tools = null) =>
        new(provider, store, catalog, new ToolRegistry(tools ?? []), new ScriptedApprovalService(), new ToolPermissionMemory(),
            new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            composer: Composer, memoryStore: memory);

    /// <summary>可指定 <see cref="ToolDescriptor.RequiresMemory"/> 的最小工具。</summary>
    private sealed class ProbeTool(string name, bool requiresMemory) : ToolBase<NoArgs>
    {
        public int Calls { get; private set; }

        public override ToolDescriptor Descriptor { get; } =
            new(name, "探測", "測試用。", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5), RequiresMemory: requiresMemory);

        protected override Task<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ToolResult.Ok("ok"));
        }
    }

    private static IEnumerable<string> ToolNames(ChatRequest request) => request.Tools?.Select(t => t.Descriptor.Name) ?? [];

    [Fact]
    public async Task Enabled_WithMemory_RequestSystemCarriesTheBlock()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var memory = new FakeMemoryStore { Items = { Item(1, "後端工程師") } };

        await Collect(Service(provider, store, new FakeModelCatalog(), memory).SendAsync(conversation.Id, "嗨"));

        var system = Assert.Single(provider.Requests).System!;
        Assert.Contains("<lumi_memory>", system);
        Assert.Contains("[m1] [本人] 後端工程師", system);
        Assert.Equal(1, memory.ActiveReads);
    }

    [Fact]
    public async Task MemoryDisabled_RequestSystemHasNoMemory_AndStoreIsNotRead()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var memory = new FakeMemoryStore { Items = { Item(1, "後端工程師") } };
        var catalog = new FakeModelCatalog();
        await catalog.SetMemoryEnabledAsync(false);

        await Collect(Service(provider, store, catalog, memory).SendAsync(conversation.Id, "嗨"));

        var system = Assert.Single(provider.Requests).System!;
        Assert.DoesNotContain("lumi_memory", system);
        Assert.DoesNotContain("長期記憶", system);
        Assert.Equal(0, memory.ActiveReads);
    }

    [Fact]
    public async Task NoMemoryStore_RequestSystemHasNoMemory()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), memory: null).SendAsync(conversation.Id, "嗨"));

        var system = Assert.Single(provider.Requests).System!;
        Assert.DoesNotContain("lumi_memory", system);
    }

    [Fact]
    public async Task EmptyStore_StillSendsTheRules_WithoutABlock()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), new FakeMemoryStore()).SendAsync(conversation.Id, "嗨"));

        var system = Assert.Single(provider.Requests).System!;
        Assert.Contains("## 長期記憶", system);
        Assert.DoesNotContain("\n<lumi_memory>\n", system);
    }

    [Fact]
    public async Task MemoryChangedMidTurn_SystemStaysFrozen_AndTheNewMemoryRidesOnTheNextMessage()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var memory = new FakeMemoryStore { Items = { Item(1, "舊的記憶") } };
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "remember", """{"text":"嗨"}""")),
            SequenceChatProvider.Answer("好"));
        // 模擬 memory_save 的效果：工具執行時寫入新記憶
        var tool = new FakeTool("remember")
        {
            Behavior = (_, _) =>
            {
                memory.Items.Add(Item(2, "剛剛存的記憶"));
                return Task.FromResult(ToolResult.Ok("已記下"));
            },
        };

        await Collect(Service(provider, store, new FakeModelCatalog(), memory, [tool]).SendAsync(conversation.Id, "嗨"));

        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains("[m1] [本人] 舊的記憶", provider.Requests[0].System);
        Assert.DoesNotContain("剛剛存的記憶", provider.Requests[0].System);
        // thinking 簽章綁定 system：同一則對話裡 system 一個字都不能變，新的記憶改附在下一則（工具結果）訊息後面
        Assert.Equal(provider.Requests[0].System, provider.Requests[1].System);
        Assert.DoesNotContain("剛剛存的記憶", provider.Requests[1].System);
        Assert.DoesNotContain("<lumi_system_update>", provider.Requests[0].TurnSuffix ?? "");
        var update = SystemPromptUpdate.LastIn([provider.Requests[1].TurnSuffix]);
        Assert.NotNull(update);
        Assert.Contains("[m1] [本人] 舊的記憶", update);
        Assert.Contains("[m2] [本人] 剛剛存的記憶", update);
        Assert.Equal(2, memory.ActiveReads);

        // 更新跟著那則工具結果訊息存檔；之後的請求原樣帶著它，而且不會再重複附加
        var toolMessage = Assert.Single(provider.Requests[1].Messages, m => m.Role == ChatRole.Tool);
        Assert.Contains("[m2] [本人] 剛剛存的記憶", toolMessage.TurnSuffix);
    }

    [Fact]
    public async Task AnUpdateIsNotRepeated_UntilTheSystemChangesAgain()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var memory = new FakeMemoryStore { Items = { Item(1, "舊的記憶") } };
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"), SequenceChatProvider.Answer("三"), SequenceChatProvider.Answer("四"));
        var service = Service(provider, store, new FakeModelCatalog(), memory);

        await Collect(service.SendAsync(conversation.Id, "一"));
        memory.Items.Add(Item(2, "新增的記憶"));
        await Collect(service.SendAsync(conversation.Id, "二"));   // 這一則附上更新
        await Collect(service.SendAsync(conversation.Id, "三"));   // 沒有變：不再附
        memory.Items.Add(Item(3, "第二次新增"));
        await Collect(service.SendAsync(conversation.Id, "四"));   // 又變了：再附一次（最新的完整內容）

        var users = (await store.GetMessagesAsync(conversation.Id)).Where(m => m.Role == ChatRole.User).Select(m => m.TurnSuffix ?? "").ToList();
        Assert.DoesNotContain("<lumi_system_update>", users[0]);
        Assert.Contains("新增的記憶", users[1]);
        Assert.DoesNotContain("<lumi_system_update>", users[2]);
        Assert.Contains("第二次新增", users[3]);
        Assert.All(provider.Requests, r => Assert.Equal(provider.Requests[0].System, r.System));
        // 第四次請求裡，前面每一則還帶著當時的內容
        Assert.Equal(users, provider.Requests[3].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.TurnSuffix ?? ""));
    }

    [Fact]
    public async Task ANewConversation_StartsWithTheCurrentSystem()
    {
        var store = new InMemoryConversationStore();
        var first = await store.CreateAsync("一");
        var second = await store.CreateAsync("二");
        var memory = new FakeMemoryStore { Items = { Item(1, "舊的記憶") } };
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider, store, new FakeModelCatalog(), memory);

        await Collect(service.SendAsync(first.Id, "嗨"));
        memory.Items.Add(Item(2, "新的記憶"));
        await Collect(service.SendAsync(second.Id, "嗨"));

        Assert.DoesNotContain("新的記憶", provider.Requests[0].System);
        Assert.Contains("新的記憶", provider.Requests[1].System);   // 新對話從現在的記憶開始，不需要更新區塊
        Assert.DoesNotContain("<lumi_system_update>", provider.Requests[1].TurnSuffix ?? "");
    }

    [Fact]
    public async Task MemoryTools_AreAlwaysOffered_ButRefusedWhenMemoryIsOff()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "memory_probe", "{}")), SequenceChatProvider.Answer("好"));
        var catalog = new FakeModelCatalog();
        await catalog.SetMemoryEnabledAsync(false);
        var probe = new ProbeTool("memory_probe", requiresMemory: true);

        var events = await Collect(Service(provider, store, catalog, new FakeMemoryStore(), [probe]).SendAsync(conversation.Id, "記住"));

        Assert.Equal(["memory_probe"], ToolNames(provider.Requests[0]));   // 開關不能讓工具清單改變
        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.True(result.IsError);
        Assert.Contains("長期記憶已在設定裡關閉", result.Content);
        Assert.Equal(0, probe.Calls);
        Assert.IsType<Completed>(events[^1]);
    }

    [Fact]
    public async Task TurningMemoryOffMidConversation_NeverChangesTheToolList_AndTheSystemChangeRidesAsAnUpdate()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var tools = new ITool[] { new ProbeTool("memory_probe", requiresMemory: true), new ProbeTool("plain_probe", requiresMemory: false) };
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var catalog = new FakeModelCatalog();
        var service = Service(provider, store, catalog, new FakeMemoryStore(), tools);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        await catalog.SetMemoryEnabledAsync(false);
        await Collect(service.SendAsync(conversation.Id, "再一次"));

        Assert.Equal(["memory_probe", "plain_probe"], ToolNames(provider.Requests[0]));
        Assert.Equal(["memory_probe", "plain_probe"], ToolNames(provider.Requests[1]));
        Assert.Equal(provider.Requests[0].System, provider.Requests[1].System);
        Assert.Contains("<lumi_system_update>", provider.Requests[1].TurnSuffix);   // 記憶關閉後 system 該有的樣子，附在新訊息後面
    }

    [Fact]
    public async Task MemoryOff_WithoutAStore_StillSendsTheToolDefinitions()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var tools = new ITool[] { new ProbeTool("memory_probe", requiresMemory: true), new ProbeTool("plain_probe", requiresMemory: false) };
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var catalog = new FakeModelCatalog();
        await catalog.SetMemoryEnabledAsync(false);

        await Collect(Service(provider, store, catalog, memory: null, tools).SendAsync(conversation.Id, "嗨"));

        Assert.Equal(["memory_probe", "plain_probe"], ToolNames(Assert.Single(provider.Requests)));
    }
}
