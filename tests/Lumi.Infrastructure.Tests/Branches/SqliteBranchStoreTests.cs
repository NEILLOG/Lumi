using System.Globalization;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Infrastructure.Branches;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests.Branches;

public sealed class SqliteBranchStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-branch-store-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewConversationStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    private SqliteBranchStore NewStore() => new(NewConversationStore());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static DateTimeOffset T(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset Base = T("2026-11-16T09:00+08:00");

    private static async Task<string> NewChatAsync(SqliteConversationStore conversations, string title = "主線")
    {
        var conversation = await conversations.CreateAsync(title, ConversationKind.Chat);
        return conversation.Id;
    }

    /// <summary>建立一則 <see cref="ConversationKind.Branch"/> 的對話（支線列的外鍵要它先存在）。</summary>
    private static async Task<string> NewBranchConversationAsync(SqliteConversationStore conversations, string title = "支線")
    {
        var conversation = await conversations.CreateAsync(title, ConversationKind.Branch);
        return conversation.Id;
    }

    private static Branch NewBranch(
        string conversationId,
        string parentId,
        string rootId,
        int depth = 1,
        DateTimeOffset? createdAt = null,
        BranchStatus status = BranchStatus.Active,
        string? summary = null,
        DateTimeOffset? endedAt = null,
        string? deliveredMessageId = null) =>
        new(conversationId, parentId, rootId, depth, ForkMessageId: "fork-" + conversationId, ForkExcerpt: "分岔的回答",
            ForkMessageAt: Base, Quote: null, Status: status, Summary: summary, DeliveredMessageId: deliveredMessageId,
            CreatedAt: createdAt ?? Base, EndedAt: endedAt);

    [Fact]
    public async Task Add_then_get_round_trips_all_fields()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);

        // 含 null 的：Quote、Summary、DeliveredMessageId、EndedAt 都是 null
        var plainId = await NewBranchConversationAsync(conversations);
        var plain = NewBranch(plainId, root, root, createdAt: Base);
        await store.AddAsync(plain);

        var readBackPlain = await store.GetAsync(plainId);
        Assert.Equal(plain, readBackPlain);
        Assert.Null(readBackPlain!.Quote);
        Assert.Null(readBackPlain.Summary);
        Assert.Null(readBackPlain.DeliveredMessageId);
        Assert.Null(readBackPlain.EndedAt);
        Assert.Equal(BranchStatus.Active, readBackPlain.Status);

        // 全部有值的
        var fullId = await NewBranchConversationAsync(conversations);
        var full = new Branch(fullId, ParentId: root, RootId: root, Depth: 1, ForkMessageId: "msg-9", ForkExcerpt: "前面的文字",
            ForkMessageAt: Base.AddMinutes(3), Quote: "被引用的句子", Status: BranchStatus.Merged, Summary: "摘要內容",
            DeliveredMessageId: "user-7", CreatedAt: Base.AddMinutes(4), EndedAt: Base.AddMinutes(5));
        await store.AddAsync(full);

        var readBackFull = await store.GetAsync(fullId);
        Assert.Equal(full, readBackFull);
        Assert.Equal("msg-9", readBackFull!.ForkMessageId);
        Assert.Equal("前面的文字", readBackFull.ForkExcerpt);
        Assert.Equal(Base.AddMinutes(3), readBackFull.ForkMessageAt);
        Assert.Equal("被引用的句子", readBackFull.Quote);
        Assert.Equal(BranchStatus.Merged, readBackFull.Status);
        Assert.Equal("摘要內容", readBackFull.Summary);
        Assert.Equal("user-7", readBackFull.DeliveredMessageId);
        Assert.Equal(Base.AddMinutes(4), readBackFull.CreatedAt);
        Assert.Equal(Base.AddMinutes(5), readBackFull.EndedAt);
    }

    [Fact]
    public async Task Add_twice_throws()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var branchId = await NewBranchConversationAsync(conversations);
        await store.AddAsync(NewBranch(branchId, root, root));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(NewBranch(branchId, root, root, status: BranchStatus.Merged)));

        // 第一次的列不被覆蓋
        Assert.Equal(BranchStatus.Active, (await store.GetAsync(branchId))!.Status);
    }

    [Fact]
    public async Task Merge_only_changes_active()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var branchId = await NewBranchConversationAsync(conversations);
        await store.AddAsync(NewBranch(branchId, root, root));

        Assert.True(await store.MergeAsync(branchId, "第一次的摘要", Base.AddMinutes(1)));
        var merged = await store.GetAsync(branchId);
        Assert.Equal(BranchStatus.Merged, merged!.Status);
        Assert.Equal("第一次的摘要", merged.Summary);
        Assert.Equal(Base.AddMinutes(1), merged.EndedAt);

        // 已經帶回了，再帶回不改欄位
        Assert.False(await store.MergeAsync(branchId, "第二次的摘要", Base.AddMinutes(2)));
        Assert.Equal(merged, await store.GetAsync(branchId));

        // 已經帶回，不能再放棄
        Assert.False(await store.AbandonAsync(branchId, Base.AddMinutes(3)));
        Assert.Equal(merged, await store.GetAsync(branchId));
    }

    [Fact]
    public async Task Abandon_only_changes_active()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var branchId = await NewBranchConversationAsync(conversations);
        await store.AddAsync(NewBranch(branchId, root, root));

        Assert.True(await store.AbandonAsync(branchId, Base.AddMinutes(1)));
        var abandoned = await store.GetAsync(branchId);
        Assert.Equal(BranchStatus.Abandoned, abandoned!.Status);
        Assert.Null(abandoned.Summary);
        Assert.Equal(Base.AddMinutes(1), abandoned.EndedAt);

        // 第二次回 false，欄位不變
        Assert.False(await store.AbandonAsync(branchId, Base.AddMinutes(2)));
        Assert.False(await store.MergeAsync(branchId, "晚到的摘要", Base.AddMinutes(3)));
        Assert.Equal(abandoned, await store.GetAsync(branchId));
    }

    [Fact]
    public async Task Children_are_ordered_by_created_at()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var late = await NewBranchConversationAsync(conversations, "晚的");
        var early = await NewBranchConversationAsync(conversations, "早的");
        // 先加晚的，確認排序看的是建立時間而不是寫入順序
        await store.AddAsync(NewBranch(late, root, root, createdAt: Base.AddMinutes(10)));
        await store.AddAsync(NewBranch(early, root, root, createdAt: Base.AddMinutes(1)));

        var children = await store.ListChildrenAsync(root);

        Assert.Equal(new[] { early, late }, children.Select(b => b.ConversationId));
    }

    [Fact]
    public async Task ListByRoots_returns_both_depths_and_ignores_other_roots()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var rootA = await NewChatAsync(conversations, "甲");
        var rootB = await NewChatAsync(conversations, "乙");
        var childA = await NewBranchConversationAsync(conversations, "甲的第一層");
        var grandA = await NewBranchConversationAsync(conversations, "甲的第二層");
        var childB = await NewBranchConversationAsync(conversations, "乙的第一層");
        await store.AddAsync(NewBranch(childA, rootA, rootA, depth: 1, createdAt: Base));
        await store.AddAsync(NewBranch(grandA, childA, rootA, depth: 2, createdAt: Base.AddMinutes(1)));
        await store.AddAsync(NewBranch(childB, rootB, rootB, depth: 1, createdAt: Base.AddMinutes(2)));

        var result = await store.ListByRootsAsync([rootA]);

        Assert.Equal(new[] { childA, grandA }, result.Select(b => b.ConversationId));
        Assert.Equal(new[] { 1, 2 }, result.Select(b => b.Depth));
    }

    [Fact]
    public async Task ListByRoots_empty_returns_empty()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var branchId = await NewBranchConversationAsync(conversations);
        await store.AddAsync(NewBranch(branchId, root, root));

        Assert.Empty(await store.ListByRootsAsync([]));
    }

    [Fact]
    public async Task Undelivered_excludes_delivered_active_and_abandoned()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var later = await NewBranchConversationAsync(conversations, "較晚帶回");
        var earlier = await NewBranchConversationAsync(conversations, "較早帶回");
        var delivered = await NewBranchConversationAsync(conversations, "已交出");
        var active = await NewBranchConversationAsync(conversations, "進行中");
        var abandoned = await NewBranchConversationAsync(conversations, "放棄");
        await store.AddAsync(NewBranch(later, root, root, status: BranchStatus.Merged, summary: "s", endedAt: Base.AddMinutes(5)));
        await store.AddAsync(NewBranch(earlier, root, root, status: BranchStatus.Merged, summary: "s", endedAt: Base.AddMinutes(2)));
        await store.AddAsync(NewBranch(delivered, root, root, status: BranchStatus.Merged, summary: "s", endedAt: Base.AddMinutes(1), deliveredMessageId: "user-1"));
        await store.AddAsync(NewBranch(active, root, root));
        await store.AddAsync(NewBranch(abandoned, root, root, status: BranchStatus.Abandoned, endedAt: Base.AddMinutes(3)));

        var undelivered = await store.ListUndeliveredAsync(root);

        Assert.Equal(new[] { earlier, later }, undelivered.Select(b => b.ConversationId));
    }

    [Fact]
    public async Task MarkDelivered_does_not_overwrite()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var first = await NewBranchConversationAsync(conversations, "第一");
        var second = await NewBranchConversationAsync(conversations, "第二");
        await store.AddAsync(NewBranch(first, root, root, status: BranchStatus.Merged, summary: "s", endedAt: Base));
        await store.AddAsync(NewBranch(second, root, root, status: BranchStatus.Merged, summary: "s", endedAt: Base));

        await store.MarkDeliveredAsync([first], "user-1");
        await store.MarkDeliveredAsync([first, second], "user-2");

        Assert.Equal("user-1", (await store.GetAsync(first))!.DeliveredMessageId);
        Assert.Equal("user-2", (await store.GetAsync(second))!.DeliveredMessageId);
        Assert.Empty(await store.ListUndeliveredAsync(root));
    }

    [Fact]
    public async Task Deleting_root_cascades_branch_rows()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var child = await NewBranchConversationAsync(conversations);
        var grand = await NewBranchConversationAsync(conversations);
        await store.AddAsync(NewBranch(child, root, root, depth: 1));
        await store.AddAsync(NewBranch(grand, child, root, depth: 2));

        Assert.True(await conversations.DeleteAsync(root));

        // 支線列靠外鍵一併刪掉；支線對話本身還在（4.9：由刪除服務先處理）
        Assert.Null(await store.GetAsync(child));
        Assert.Null(await store.GetAsync(grand));
        Assert.Empty(await store.ListByRootsAsync([root]));
        Assert.NotNull(await conversations.GetConversationAsync(child));
        Assert.NotNull(await conversations.GetConversationAsync(grand));
    }

    [Fact]
    public async Task Orphan_lists_branch_kind_without_row()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var root = await NewChatAsync(conversations);
        var withRow = await NewBranchConversationAsync(conversations, "有列");
        var orphan = await NewBranchConversationAsync(conversations, "孤兒");
        // 一般對話沒有支線列，不算孤兒
        var plainChat = await NewChatAsync(conversations, "一般");
        await store.AddAsync(NewBranch(withRow, root, root));

        var orphans = await store.ListOrphanConversationsAsync();

        Assert.Equal(new[] { orphan }, orphans);
        Assert.DoesNotContain(plainChat, orphans);
        Assert.DoesNotContain(withRow, orphans);
    }

    [Fact]
    public async Task Branch_kind_is_hidden_from_list_search_and_latest()
    {
        var conversations = NewConversationStore();
        var chat = await conversations.CreateAsync("一般");
        await conversations.AppendMessageAsync(chat.Id, UserText("天氣預報的內容"));
        await Task.Delay(5);
        var branch = await conversations.CreateAsync("支線", ConversationKind.Branch);
        await conversations.AppendMessageAsync(branch.Id, UserText("天氣預報的內容"));

        var list = await conversations.ListAsync(10);
        var hits = await conversations.SearchAsync("天氣預報", 10);
        var latest = await conversations.GetLatestAsync();

        Assert.Equal(new[] { chat.Id }, list.Select(c => c.Id));
        Assert.Equal(new[] { chat.Id }, hits.Select(h => h.Conversation.Id));
        Assert.NotNull(latest);
        Assert.Equal(chat.Id, latest.Id);

        // 直接讀取還找得到，而且種類是支線
        var found = await conversations.GetConversationAsync(branch.Id);
        Assert.NotNull(found);
        Assert.Equal(ConversationKind.Branch, found.Kind);
    }

    private static ChatMessage UserText(string text) =>
        new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);
}
