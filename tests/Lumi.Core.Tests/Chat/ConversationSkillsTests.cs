using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Skills;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>技能清單的接線（M29 29B，E2）：只有工具快照含 <c>read_skill</c> 才帶清單；技能變動走 <c>SystemPromptUpdate</c>；讀取失敗不擋送出。</summary>
public class ConversationSkillsTests
{
    private static readonly PromptComposer Composer = new([
        new BasePromptLayer(), new PersonaPromptLayer(), new ModeFormatPromptLayer(),
        new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()), new SkillsPromptLayer(),
    ]);

    /// <summary>所有方法都丟例外（只有 <see cref="ListAsync"/> 是要測的故障），證明對話不會因為技能清單失敗而失敗。</summary>
    private sealed class BrokenSkillStore : ISkillStore
    {
        public event EventHandler? Changed { add { } remove { } }

        public string Folder => "";

        public Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken ct) => throw new IOException("技能資料夾讀取失敗");

        public Task<SkillDocument?> GetAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ReadFileAsync(string name, string relativePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<SkillSaveResult> SaveAsync(SkillDraft draft, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<SkillImportPreview> PreviewImportAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<SkillSaveResult> InstallAsync(SkillImportPreview preview, CancellationToken ct) => throw new NotSupportedException();
        public Task DiscardImportAsync(SkillImportPreview preview, CancellationToken ct) => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ConversationService Service(IChatProvider provider, IConversationStore store, FakeModelCatalog catalog, ISkillStore? skills) =>
        new(provider, store, catalog, new ToolRegistry([]), new ScriptedApprovalService(), new ToolPermissionMemory(),
            new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            composer: Composer, skills: skills);

    /// <summary>對話的工具快照：有 <c>read_skill</c> 就只放它，否則放別的工具。</summary>
    private static async Task SnapshotAsync(IConversationStore store, string conversationId, params string[] names)
    {
        var tools = names.Select(n => ToolDefinition.From(new FakeTool(n))).ToList();
        await store.SetToolSnapshotAsync(conversationId, ToolSnapshotJson.Serialize(tools));
    }

    private static InMemorySkillStore SkillsWith(params (string Name, string Description)[] skills)
    {
        var store = new InMemorySkillStore();
        foreach (var (name, description) in skills)
            store.Add(new SkillDraft(name, description, "步驟"));
        return store;
    }

    private static FakeModelCatalog CatalogWithToolsDisabled()
    {
        var catalog = new FakeModelCatalog();
        catalog.Current = new AppSettings(
            [FakeModelCatalog.Connection], [FakeModelCatalog.Model with { ToolsEnabled = false }],
            FakeModelCatalog.Model.Id, FakeModelCatalog.Model.Id, ThemePreference.System);
        return catalog;
    }

    [Fact]
    public async Task SnapshotWithReadSkill_CarriesTheUsableSkillList_InSystem()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await SnapshotAsync(store, conversation.Id, "read_skill", "echo");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), SkillsWith(("週報", "每週五整理本週工作"))).SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(provider.Requests);
        Assert.Contains("## 技能\n", request.System);
        Assert.Contains("- 週報：每週五整理本週工作", request.System);
    }

    [Fact]
    public async Task SnapshotWithoutReadSkill_DoesNotCarryTheList_EvenWhenSkillsExist()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await SnapshotAsync(store, conversation.Id, "echo");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), SkillsWith(("週報", "每週五"))).SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(provider.Requests);
        Assert.DoesNotContain("## 技能", request.System);
    }

    [Fact]
    public async Task ToolsDisabledForTheModel_DoesNotCarryTheList()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, CatalogWithToolsDisabled(), SkillsWith(("週報", "每週五"))).SendAsync(conversation.Id, "嗨"));

        var request = Assert.Single(provider.Requests);
        Assert.Null(request.Tools);
        Assert.DoesNotContain("## 技能", request.System);
    }

    [Fact]
    public async Task SkillsChangedMidConversation_ArriveOnTheNextSend_ThroughSystemPromptUpdate()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await SnapshotAsync(store, conversation.Id, "read_skill");
        var skills = SkillsWith(("週報", "每週五"));
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var service = Service(provider, store, new FakeModelCatalog(), skills);

        await Collect(service.SendAsync(conversation.Id, "嗨"));
        skills.Add(new SkillDraft("會議", "開會後整理紀錄", "步驟"));
        await Collect(service.SendAsync(conversation.Id, "再一次"));

        // system 在一則對話內固定：第一次送出的內容不變，新的清單以更新區塊附在下一則訊息後面
        Assert.Equal(provider.Requests[0].System, provider.Requests[1].System);
        Assert.DoesNotContain("會議", provider.Requests[1].System);
        var update = SystemPromptUpdate.LastIn([provider.Requests[1].TurnSuffix]);
        Assert.NotNull(update);
        Assert.Contains("- 會議：開會後整理紀錄", update);
        Assert.Contains("- 週報：每週五", update);
    }

    [Fact]
    public async Task SkillsStoreThrows_TheConversationStillCompletes_WithoutTheList()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await SnapshotAsync(store, conversation.Id, "read_skill");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        var events = await Collect(Service(provider, store, new FakeModelCatalog(), new BrokenSkillStore()).SendAsync(conversation.Id, "嗨"));

        Assert.IsType<Completed>(Assert.Single(events.OfType<Completed>()));
        Assert.Empty(events.OfType<Failed>());
        var request = Assert.Single(provider.Requests);
        Assert.DoesNotContain("## 技能", request.System);
        var stored = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public async Task NoUsableSkills_NoSection()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await SnapshotAsync(store, conversation.Id, "read_skill");
        var skills = new InMemorySkillStore();
        skills.Add(new SkillDraft("停用的", "說明", "步驟"), enabled: false);
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog(), skills).SendAsync(conversation.Id, "嗨"));

        Assert.DoesNotContain("## 技能", Assert.Single(provider.Requests).System);
    }
}
