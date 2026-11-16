using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>M35 計畫 4.8：有研究本的對話，主 AI 在目標或已決定變了時收到錨（<c>&lt;lumi_note_anchor&gt;</c>）。</summary>
public class ConversationNoteAnchorTests
{
    private const string Open = "<lumi_note_anchor>";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new ModeFormatPromptLayer(), new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
    ]);

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ConversationService Service(IChatProvider provider, InMemoryConversationStore store, FakeModelCatalog catalog,
        InMemoryContextNoteStore? notes, IEnumerable<ITool>? tools = null) =>
        new(provider, store, catalog, new ToolRegistry(tools ?? []), new ScriptedApprovalService(), new ToolPermissionMemory(),
            new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            composer: Composer, contextNotes: notes);

    /// <summary>含決定（每行一條）的研究本內容。</summary>
    private static string Decided(params string[] decided) =>
        ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Decided, decided.Select(d => new NoteBlock(d)).ToArray()).ToMarkdown();

    private static ContextNoteRevision Revision(string noteId, int version, string goal, string content) =>
        new(ContextNoteRevision.NewId(), noteId, version, version == 1 ? ContextNoteRevisionKind.StartBlank : ContextNoteRevisionKind.UserEdit,
            goal, content, [], null, 0, Now);

    /// <summary>建立這則對話的研究本與第一版，回傳研究本。</summary>
    private static async Task<ContextNote> CreateNoteAsync(InMemoryContextNoteStore notes, string conversationId, string goal, string content)
    {
        var note = new ContextNote(ContextNote.NewId(), conversationId, goal, true, 1, 0, null, null, Now, Now);
        await notes.CreateAsync(note, Revision(note.Id, 1, goal, content));
        return note;
    }

    /// <summary>請求裡所有錨的出現次數（系統、每一則訊息的後綴）。最後一則的後綴就是請求尾端的內容，提供者只送一次，所以不另外加。</summary>
    private static int AnchorCount(ChatRequest request)
    {
        var all = string.Concat(request.Messages.Select(m => m.TurnSuffix ?? "").Append(request.System ?? ""));
        return all.Split(Open).Length - 1;
    }

    private static async Task<string?> StoredSuffixOfLastUserMessage(InMemoryConversationStore store, string conversationId) =>
        (await store.GetMessagesAsync(conversationId)).Where(m => m.Role == ChatRole.User).Select(m => m.TurnSuffix).Last();

    [Fact]
    public async Task WithoutANote_TheSuffixIsOnlyTheMode()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), notes: new InMemoryContextNoteStore()).SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(provider.Requests);
        Assert.StartsWith("<lumi_mode>\n目前模式：工作台", request.TurnSuffix);
        Assert.DoesNotContain(Open, request.TurnSuffix);
        Assert.Equal(request.TurnSuffix, await StoredSuffixOfLastUserMessage(store, conversation.Id));
    }

    [Fact]
    public async Task FirstTurn_AttachesTheAnchor_AndStoresItOnTheUserMessage()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), notes).SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(provider.Requests);
        Assert.StartsWith("<lumi_mode>\n目前模式：工作台", request.TurnSuffix);
        Assert.Contains("\n\n<lumi_note_anchor>", request.TurnSuffix);
        Assert.Contains("<goal>\n找出快取策略\n</goal>", request.TurnSuffix);
        Assert.Contains("<decided>\n用 SQLite\n</decided>", request.TurnSuffix);
        Assert.DoesNotContain("<note>", request.TurnSuffix);   // 只有摘要整理之後才附整本
        Assert.Equal(request.TurnSuffix, await StoredSuffixOfLastUserMessage(store, conversation.Id));
    }

    [Fact]
    public async Task SecondTurn_WithoutAChange_DoesNotAttachAgain()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider, store, new FakeModelCatalog(), notes);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        await Collect(service.SendAsync(conversation.Id, "再一次"));

        Assert.Equal(1, AnchorCount(provider.Requests[0]));
        Assert.Equal(1, AnchorCount(provider.Requests[1]));   // 只有第一則歷史裡的那個，這一則沒有新的
        Assert.DoesNotContain(Open, provider.Requests[1].TurnSuffix);
    }

    [Fact]
    public async Task GoalChange_AttachesTheAnchorAgain()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        var note = await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider, store, new FakeModelCatalog(), notes);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        await notes.AddRevisionAsync(Revision(note.Id, 2, "改找出索引策略", Decided("用 SQLite")));
        await Collect(service.SendAsync(conversation.Id, "再一次"));

        Assert.Contains("<goal>\n改找出索引策略\n</goal>", provider.Requests[1].TurnSuffix);
        Assert.Contains(Open, await StoredSuffixOfLastUserMessage(store, conversation.Id));
    }

    [Fact]
    public async Task NewDecision_AttachesTheAnchorAgain()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        var note = await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider, store, new FakeModelCatalog(), notes);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        await notes.AddRevisionAsync(Revision(note.Id, 2, "找出快取策略", Decided("用 SQLite", "快取時間定 5 分鐘")));
        await Collect(service.SendAsync(conversation.Id, "再一次"));

        Assert.Contains("<decided>\n用 SQLite\n快取時間定 5 分鐘\n</decided>", provider.Requests[1].TurnSuffix);
    }

    [Fact]
    public async Task ToolLoop_TheAnchorAppearsOnlyOnce_InEveryCall()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "echo", """{"text":"嗨"}""")),
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t2", "echo", """{"text":"再"}""")),
            SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), notes, tools: [new FakeTool()]).SendAsync(conversation.Id, "嗨"));

        Assert.Equal(3, provider.Requests.Count);
        Assert.All(provider.Requests, r => Assert.Equal(1, AnchorCount(r)));
    }

    [Fact]
    public async Task Retry_DoesNotAttachTwice()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(
            [new Failed(new ChatFailure(ChatFailureKind.Network, "斷線", Retryable: true))], SequenceChatProvider.Answer("好"));
        var service = Service(provider, store, new FakeModelCatalog(), notes);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        await Collect(service.RetryAsync(conversation.Id));

        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(1, AnchorCount(provider.Requests[1]));
        Assert.Equal(provider.Requests[0].TurnSuffix, await StoredSuffixOfLastUserMessage(store, conversation.Id));
    }

    [Fact]
    public async Task SpriteMode_DoesNotAttach()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), notes).SendAsync(conversation.Id, "嗨", mode: ChatMode.Sprite));

        var request = Assert.Single(provider.Requests);
        Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", request.TurnSuffix);
        Assert.Equal(0, AnchorCount(request));
    }

    [Fact]
    public async Task ScheduledRun_DoesNotAttach()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("排程");
        var notes = new InMemoryContextNoteStore();
        await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var run = new ConversationRunOptions(new ScheduledRunInfo("每天", Now, Now));

        await Collect(Service(provider, store, new FakeModelCatalog(), notes).SendAsync(conversation.Id, "嗨", options: run));

        Assert.Equal(0, AnchorCount(Assert.Single(provider.Requests)));
    }

    [Fact]
    public async Task AfterASummary_TheFullNoteIsAttachedOnce_ThenOnlyTheCoreWhenItChanges()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        var note = await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"), SequenceChatProvider.Answer("三"), SequenceChatProvider.Answer("四"));
        var service = Service(provider, store, new FakeModelCatalog(), notes);

        await Collect(service.SendAsync(conversation.Id, "第一則"));
        // 摘要整理：第一則使用者訊息與它的回答（seq 1、2）被整理掉，原來的錨也跟著不在送出的訊息裡
        await store.AddCompactionAsync(conversation.Id, new CompactionRecord(CompactionRecord.NewId(), CompactionKind.Summary, 2, "摘要", null, Now));
        await Collect(service.SendAsync(conversation.Id, "第二則"));

        Assert.Contains("<note>\n", provider.Requests[1].TurnSuffix);
        Assert.Contains("用 SQLite", provider.Requests[1].TurnSuffix);

        // 之後沒有變動：這一則不再附（歷史裡第二則那個錨是原樣帶回的）
        await Collect(service.SendAsync(conversation.Id, "第三則"));
        Assert.DoesNotContain(Open, provider.Requests[2].TurnSuffix);

        // 之後目標變了：只附核心，不再附整本
        await notes.AddRevisionAsync(Revision(note.Id, 2, "改找出索引策略", Decided("用 SQLite")));
        await Collect(service.SendAsync(conversation.Id, "第四則"));
        Assert.Contains("<goal>\n改找出索引策略\n</goal>", provider.Requests[3].TurnSuffix);
        Assert.DoesNotContain("<note>", provider.Requests[3].TurnSuffix);
    }

    [Fact]
    public async Task AnOversizedNote_IsNotAttachedInFull()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        var big = ContextNoteDocument.Empty
            .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock(new string('字', 13_000))])
            .WithSection(ContextNoteDocument.Decided, [new NoteBlock("用 SQLite")])
            .ToMarkdown();
        await CreateNoteAsync(notes, conversation.Id, "找出快取策略", big);
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider, store, new FakeModelCatalog(), notes);

        await Collect(service.SendAsync(conversation.Id, "第一則"));
        await store.AddCompactionAsync(conversation.Id, new CompactionRecord(CompactionRecord.NewId(), CompactionKind.Summary, 2, "摘要", null, Now));
        await Collect(service.SendAsync(conversation.Id, "第二則"));

        var suffix = provider.Requests[1].TurnSuffix;
        Assert.Contains("<decided>\n用 SQLite\n</decided>", suffix);   // 核心照附
        Assert.DoesNotContain("<note>", suffix);
    }

    [Fact]
    public async Task WithASystemPromptUpdate_BothBlocks_AreAttached_InAFixedOrder()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var notes = new InMemoryContextNoteStore();
        var note = await CreateNoteAsync(notes, conversation.Id, "找出快取策略", Decided("用 SQLite"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var catalog = new FakeModelCatalog();
        var service = Service(provider, store, catalog, notes);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        await catalog.SetPersonaAsync(new PersonaSettings(PersonaStyle.Professional, Proactivity.Reserved, "叫我 Neil"));
        await notes.AddRevisionAsync(Revision(note.Id, 2, "改找出索引策略", Decided("用 SQLite")));
        await Collect(service.SendAsync(conversation.Id, "再一次"));

        var suffix = provider.Requests[1].TurnSuffix!;
        var update = suffix.IndexOf("<lumi_system_update>", StringComparison.Ordinal);
        var anchor = suffix.IndexOf(Open, StringComparison.Ordinal);
        Assert.True(update >= 0);
        Assert.True(anchor > update);   // SystemPromptUpdate 在前
        Assert.Contains("叫我 Neil", suffix);
        Assert.Contains("<goal>\n改找出索引策略\n</goal>", suffix);
    }
}
