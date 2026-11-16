using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Branches;

/// <summary>M36 包 36E：<see cref="BranchService"/> 的開、帶回、放棄、整理、狀態與孤兒清理（計畫 4.4～4.6）。</summary>
public class BranchServiceTests
{
    /// <summary>記下每次整理的請求，回傳固定的摘要。</summary>
    private sealed class FakeSummarizer : IBranchSummarizer
    {
        public List<BranchSummaryRequest> Requests { get; } = [];

        public BranchSummaryDraft Next { get; set; } = new("整理好的摘要", null);

        public Task<BranchSummaryDraft> SummarizeAsync(BranchSummaryRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(Next);
        }
    }

    /// <summary>會回報的孤兒清單是固定的；其他成員委派給記憶體版。</summary>
    private sealed class OrphanBranchStore(InMemoryBranchStore inner, IReadOnlyList<string> orphans) : IBranchStore
    {
        public Task AddAsync(Branch branch, CancellationToken ct = default) => inner.AddAsync(branch, ct);
        public Task<Branch?> GetAsync(string conversationId, CancellationToken ct = default) => inner.GetAsync(conversationId, ct);
        public Task<IReadOnlyList<Branch>> ListChildrenAsync(string parentId, CancellationToken ct = default) => inner.ListChildrenAsync(parentId, ct);
        public Task<IReadOnlyList<Branch>> ListByRootsAsync(IReadOnlyCollection<string> rootIds, CancellationToken ct = default) => inner.ListByRootsAsync(rootIds, ct);
        public Task<bool> MergeAsync(string conversationId, string summary, DateTimeOffset endedAt, CancellationToken ct = default) =>
            inner.MergeAsync(conversationId, summary, endedAt, ct);
        public Task<bool> AbandonAsync(string conversationId, DateTimeOffset endedAt, CancellationToken ct = default) => inner.AbandonAsync(conversationId, endedAt, ct);
        public Task<IReadOnlyList<Branch>> ListUndeliveredAsync(string parentId, CancellationToken ct = default) => inner.ListUndeliveredAsync(parentId, ct);
        public Task MarkDeliveredAsync(IReadOnlyCollection<string> conversationIds, string messageId, CancellationToken ct = default) =>
            inner.MarkDeliveredAsync(conversationIds, messageId, ct);
        public Task<IReadOnlyList<string>> ListOrphanConversationsAsync(CancellationToken ct = default) => Task.FromResult(orphans);
    }

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public InMemoryBranchStore Branches { get; } = new();
        public InMemoryConversationContextStore Contexts { get; } = new();
        public FakeSummarizer Summarizer { get; } = new();
        public SpriteConversationService Sprite { get; }
        public BranchService Service { get; }

        public Rig()
        {
            Sprite = new SpriteConversationService(Store);
            Service = new BranchService(Store, Branches, Contexts, Summarizer, Sprite, TimeProvider.System, NullLogger<BranchService>.Instance);
        }
    }

    private static async Task<ChatMessage> AddAsync(InMemoryConversationStore store, string conversationId, ChatRole role, string text)
    {
        var message = new ChatMessage(ChatMessage.NewId(), role, [new TextBlock(text)], DateTimeOffset.UtcNow);
        await store.AppendMessageAsync(conversationId, message);
        return message;
    }

    /// <summary>主線（一問一答）與它的一則回答；回傳主線與回答。</summary>
    private static async Task<(Conversation Main, ChatMessage Answer)> MainWithAnswerAsync(Rig rig, string title = "主線")
    {
        var main = await rig.Store.CreateAsync(title);
        await AddAsync(rig.Store, main.Id, ChatRole.User, "第一個問題");
        var answer = await AddAsync(rig.Store, main.Id, ChatRole.Assistant, "分岔的回答內容");
        return (main, answer);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        var ex = await Assert.ThrowsAsync<T>(action);
        return ex;
    }

    [Fact]
    public async Task Start_creates_branch_with_copied_system_tools_and_scope()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        await rig.Store.SetSystemPromptAsync(main.Id, "系統提示");
        await rig.Store.SetToolSnapshotAsync(main.Id, "[{\"name\":\"read_file\"}]");
        await rig.Contexts.AddAsync(main.Id, ContextKind.File, @"C:\資料\報告.txt", "報告.txt");

        var child = await rig.Service.StartAsync(main.Id, answer.Id, "選取的文字");

        Assert.Equal(ConversationKind.Branch, child.Kind);
        Assert.Equal("系統提示", await rig.Store.GetSystemPromptAsync(child.Id));
        Assert.Equal("[{\"name\":\"read_file\"}]", await rig.Store.GetToolSnapshotAsync(child.Id));

        var branch = await rig.Branches.GetAsync(child.Id);
        Assert.NotNull(branch);
        Assert.Equal(BranchStatus.Active, branch.Status);
        Assert.Equal(main.Id, branch.ParentId);
        Assert.Equal(main.Id, branch.RootId);
        Assert.Equal(1, branch.Depth);
        Assert.Equal(answer.Id, branch.ForkMessageId);
        Assert.Equal("分岔的回答內容", branch.ForkExcerpt);
        Assert.Equal("選取的文字", branch.Quote);

        // 上一層的範圍複製進支線，而且標成已送出
        var scope = await rig.Contexts.GetAsync(child.Id);
        var copied = Assert.Single(scope);
        Assert.Equal("報告.txt", copied.DisplayName);
        Assert.False(copied.IsPending);
    }

    [Fact]
    public async Task Start_title_from_quote_or_default()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);

        var plain = await rig.Service.StartAsync(main.Id, answer.Id, null);
        Assert.Equal(BranchLimits.DefaultTitle, (await rig.Store.GetConversationAsync(plain.Id))!.Title);

        await rig.Service.AbandonAsync(plain.Id);
        var longQuote = new string('長', 40);
        var quoted = await rig.Service.StartAsync(main.Id, answer.Id, longQuote);
        var title = (await rig.Store.GetConversationAsync(quoted.Id))!.Title;
        Assert.Equal(BranchBlocks.TitleFromQuote(longQuote), title);
        Assert.EndsWith("…", title);
        Assert.Equal(BranchLimits.TitleFromQuoteChars + 1, title.Length);
    }

    [Fact]
    public async Task Start_blocks()
    {
        // 上一層不是對話
        var rig = new Rig();
        var missing = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync("沒有這則", "x", null));
        Assert.Equal(BranchMessages.NotBranchableMessage, missing.Message);

        // 排程的執行紀錄不能開支線
        rig = new Rig();
        var scheduled = await rig.Store.CreateAsync("排程", ConversationKind.ScheduledRun);
        var scheduledAnswer = await AddAsync(rig.Store, scheduled.Id, ChatRole.Assistant, "回答");
        var notBranchable = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(scheduled.Id, scheduledAnswer.Id, null));
        Assert.Equal(BranchMessages.NotBranchableMessage, notBranchable.Message);

        // 小精靈的專屬對話不能開支線
        rig = new Rig();
        var sprite = await rig.Sprite.GetOrCreateAsync("小精靈");
        var spriteAnswer = await AddAsync(rig.Store, sprite.Id, ChatRole.Assistant, "回答");
        var spriteBlocked = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(sprite.Id, spriteAnswer.Id, null));
        Assert.Equal(BranchMessages.NotBranchableMessage, spriteBlocked.Message);

        // 上一層是已經結束的支線
        rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        var done = await rig.Service.StartAsync(main.Id, answer.Id, null);
        await rig.Service.AbandonAsync(done.Id);
        var finished = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(done.Id, answer.Id, null));
        Assert.Equal(BranchMessages.FinishedParentMessage, finished.Message);

        // 已經有兩層：第二層底下不能再開
        rig = new Rig();
        (main, answer) = await MainWithAnswerAsync(rig);
        var first = await rig.Service.StartAsync(main.Id, answer.Id, null);
        var firstAnswer = await AddAsync(rig.Store, first.Id, ChatRole.Assistant, "第一層的回答");
        var second = await rig.Service.StartAsync(first.Id, firstAnswer.Id, null);
        var secondAnswer = await AddAsync(rig.Store, second.Id, ChatRole.Assistant, "第二層的回答");
        var depth = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(second.Id, secondAnswer.Id, null));
        Assert.Equal(BranchMessages.DepthMessage, depth.Message);

        // 上一層已經有進行中的下一層
        rig = new Rig();
        (main, answer) = await MainWithAnswerAsync(rig);
        await rig.Service.StartAsync(main.Id, answer.Id, null);
        var another = await AddAsync(rig.Store, main.Id, ChatRole.Assistant, "另一則回答");
        var locked = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(main.Id, another.Id, null));
        Assert.Equal(BranchMessages.ActiveChildMessage, locked.Message);

        // 分岔的那則不在上一層裡，或不是助理的回答
        rig = new Rig();
        (main, answer) = await MainWithAnswerAsync(rig);
        var notFound = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(main.Id, "沒有這則", null));
        Assert.Equal(BranchMessages.MessageNotFoundMessage, notFound.Message);
        var userMessage = (await rig.Store.GetMessagesAsync(main.Id))[0];
        var userFork = await ThrowsAsync<InvalidOperationException>(() => rig.Service.StartAsync(main.Id, userMessage.Id, null));
        Assert.Equal(BranchMessages.MessageNotFoundMessage, userFork.Message);

        // CheckStartAsync 只回答原因，不建立任何東西
        Assert.Equal(BranchStartBlock.MessageNotFound, await rig.Service.CheckStartAsync(main.Id, "沒有這則"));
        Assert.Equal(BranchStartBlock.None, await rig.Service.CheckStartAsync(main.Id, answer.Id));
        Assert.Empty(await rig.Branches.ListChildrenAsync(main.Id));
    }

    [Fact]
    public async Task Start_from_sprite_conversation_is_not_branchable()
    {
        var rig = new Rig();
        var sprite = await rig.Sprite.GetOrCreateAsync("小精靈");
        var answer = await AddAsync(rig.Store, sprite.Id, ChatRole.Assistant, "回答");

        Assert.Equal(BranchStartBlock.NotBranchable, await rig.Service.CheckStartAsync(sprite.Id, answer.Id));
    }

    [Fact]
    public async Task Merge_requires_text_and_limit()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        var child = await rig.Service.StartAsync(main.Id, answer.Id, null);

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.MergeAsync(child.Id, "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Service.MergeAsync(child.Id, new string('字', BranchLimits.SummaryMaxChars + 1)));

        // 沒有被拒絕，支線還是進行中
        Assert.Equal(BranchStatus.Active, (await rig.Branches.GetAsync(child.Id))!.Status);
    }

    [Fact]
    public async Task Merge_and_abandon_raise_changed_and_return_parent()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        var events = new List<BranchChangedArgs>();
        rig.Service.Changed += (_, e) => events.Add(e);

        var merged = await rig.Service.StartAsync(main.Id, answer.Id, null);
        var mergedParent = await rig.Service.MergeAsync(merged.Id, "  帶回的摘要  ");
        Assert.Equal(main.Id, mergedParent.Id);
        var mergedRow = (await rig.Branches.GetAsync(merged.Id))!;
        Assert.Equal(BranchStatus.Merged, mergedRow.Status);
        Assert.Equal("帶回的摘要", mergedRow.Summary);
        Assert.Null(mergedRow.DeliveredMessageId);

        var abandoned = await rig.Service.StartAsync(main.Id, answer.Id, null);
        var abandonedParent = await rig.Service.AbandonAsync(abandoned.Id);
        Assert.Equal(main.Id, abandonedParent.Id);
        Assert.Equal(BranchStatus.Abandoned, (await rig.Branches.GetAsync(abandoned.Id))!.Status);

        // 開、帶回、放棄各發一次；事件裡有上一層與狀態
        Assert.Equal(
            new[] { BranchStatus.Active, BranchStatus.Merged, BranchStatus.Active, BranchStatus.Abandoned },
            events.Select(e => e.Status).ToArray());
        Assert.All(events.Where(e => e.Status != BranchStatus.Active), e => Assert.Equal(main.Id, e.ParentId));
    }

    [Fact]
    public async Task Merge_twice_throws()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        var child = await rig.Service.StartAsync(main.Id, answer.Id, null);
        await rig.Service.MergeAsync(child.Id, "摘要");

        var ex = await ThrowsAsync<InvalidOperationException>(() => rig.Service.MergeAsync(child.Id, "再一次"));
        Assert.Equal(BranchMessages.FinishedBranchMessage, ex.Message);
    }

    [Fact]
    public async Task Draft_passes_fork_quote_main_question_and_child_summaries()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        await AddAsync(rig.Store, main.Id, ChatRole.User, "主線最後的問題");
        var child = await rig.Service.StartAsync(main.Id, answer.Id, "選取的文字");
        await AddAsync(rig.Store, child.Id, ChatRole.User, "支線的問題");
        await AddAsync(rig.Store, child.Id, ChatRole.Assistant, "支線的回答");

        // 支線再分出一條，帶回；還沒交出，所以會交給這條支線的整理
        var childAnswer = (await rig.Store.GetMessagesAsync(child.Id))[^1];
        var grandchild = await rig.Service.StartAsync(child.Id, childAnswer.Id, null);
        await rig.Service.MergeAsync(grandchild.Id, "孫摘要");

        var draft = await rig.Service.DraftSummaryAsync(child.Id);

        Assert.Equal("整理好的摘要", draft.Text);
        var request = Assert.Single(rig.Summarizer.Requests);
        Assert.Equal(main.Id, request.RootId);
        Assert.Equal("選取的文字", request.Quote);
        Assert.Equal("主線最後的問題", request.MainLastUserText);
        Assert.Contains("分岔的回答內容", request.ForkText);
        Assert.Equal(2, request.BranchMessages.Count);
        Assert.Equal(new[] { (BranchLimits.DefaultTitle, "孫摘要") }, request.ChildSummaries);
    }

    [Fact]
    public async Task Draft_empty_branch_fails()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        var child = await rig.Service.StartAsync(main.Id, answer.Id, null);

        var draft = await rig.Service.DraftSummaryAsync(child.Id);

        Assert.Null(draft.Text);
        Assert.Equal(BranchMessages.BranchEmptyMessage, draft.Failure!.Message);
        Assert.Empty(rig.Summarizer.Requests);
    }

    [Fact]
    public async Task Find_active_leaf_two_levels()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig);
        var first = await rig.Service.StartAsync(main.Id, answer.Id, null);
        var firstAnswer = await AddAsync(rig.Store, first.Id, ChatRole.Assistant, "第一層的回答");
        var second = await rig.Service.StartAsync(first.Id, firstAnswer.Id, null);

        // 最深的進行中支線
        Assert.Equal(second.Id, (await rig.Service.FindActiveLeafAsync(main.Id))!.Id);
        Assert.Equal(second.Id, (await rig.Service.FindActiveLeafAsync(first.Id))!.Id);
        // 支線裡沒有下一層
        Assert.Null(await rig.Service.FindActiveLeafAsync(second.Id));
    }

    [Fact]
    public async Task State_path_and_lock()
    {
        var rig = new Rig();
        var (main, answer) = await MainWithAnswerAsync(rig, "主線標題");
        var first = await rig.Service.StartAsync(main.Id, answer.Id, null);
        var firstAnswer = await AddAsync(rig.Store, first.Id, ChatRole.Assistant, "第一層的回答");
        var second = await rig.Service.StartAsync(first.Id, firstAnswer.Id, null);

        var deepest = await rig.Service.GetStateAsync(second.Id);
        Assert.Equal(new[] { main.Id, first.Id, second.Id }, deepest.Path.Select(n => n.ConversationId).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, deepest.Path.Select(n => n.Depth).ToArray());
        Assert.Equal("主線標題", deepest.Root.Title);
        Assert.Null(deepest.Path[0].Status);
        Assert.Equal(BranchStatus.Active, deepest.Path[2].Status);
        Assert.True(deepest.IsBranch);
        Assert.Equal(2, deepest.Depth);
        Assert.Empty(deepest.Children);
        Assert.False(deepest.IsLocked);

        var middle = await rig.Service.GetStateAsync(first.Id);
        Assert.Equal(second.Id, Assert.Single(middle.Children).ConversationId);
        Assert.Equal(second.Id, middle.ActiveChild!.ConversationId);
        Assert.True(middle.IsLocked);

        var root = await rig.Service.GetStateAsync(main.Id);
        Assert.False(root.IsBranch);
        Assert.Equal(0, root.Depth);
        Assert.Single(root.Path);
        Assert.Equal(first.Id, root.ActiveChild!.ConversationId);
    }

    [Fact]
    public async Task Cleanup_deletes_orphans()
    {
        // 建立到一半中斷留下的：kind 是支線，但沒有支線的列（儲存回報它是孤兒）
        var store = new InMemoryConversationStore();
        var orphan = await store.CreateAsync("沒有完成的支線", ConversationKind.Branch);
        var keep = await store.CreateAsync("正常的對話");
        var service = new BranchService(store, new OrphanBranchStore(new InMemoryBranchStore(), [orphan.Id]), new InMemoryConversationContextStore(),
            new FakeSummarizer(), new SpriteConversationService(store), TimeProvider.System, NullLogger<BranchService>.Instance);

        await service.CleanupOrphansAsync();

        Assert.Null(await store.GetConversationAsync(orphan.Id));
        Assert.NotNull(await store.GetConversationAsync(keep.Id));
    }
}
