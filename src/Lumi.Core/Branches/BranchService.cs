using Lumi.Core.Chat;
using Lumi.Core.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Branches;

/// <summary>支線的開、帶回、放棄與狀態（計畫 4.4～4.6）。檢查與建立都在這裡，對話流程的擋下規則在 <c>ConversationService</c>。</summary>
public sealed class BranchService(
    IConversationStore conversations, IBranchStore branches, IConversationContextStore contexts, IBranchSummarizer summarizer,
    SpriteConversationService sprite, TimeProvider time, ILogger<BranchService>? logger = null) : IBranchService
{
    /// <summary>這則不是支線（給帶回、放棄用；開支線的「不能開」有自己的 <see cref="BranchStartBlock.NotBranchable"/>）。</summary>
    private const string NotABranchMessage = "這則對話不是支線。";

    private readonly ILogger<BranchService> _logger = logger ?? NullLogger<BranchService>.Instance;

    public event EventHandler<BranchChangedArgs>? Changed;

    public async Task<BranchState> GetStateAsync(string conversationId, CancellationToken ct = default)
    {
        // 由自己往上走到主線；路徑是由下往上收集的，最後反過來（主線在前）
        var self = await branches.GetAsync(conversationId, ct).ConfigureAwait(false);
        var path = new List<BranchPathNode>();
        var id = conversationId;
        var node = self;
        while (true)
        {
            path.Add(new BranchPathNode(id, await TitleOfAsync(id, ct).ConfigureAwait(false), node?.Depth ?? 0, node?.Status));
            if (node is null)
                break;
            id = node.ParentId;
            node = await branches.GetAsync(id, ct).ConfigureAwait(false);
        }
        path.Reverse();

        var children = await branches.ListChildrenAsync(conversationId, ct).ConfigureAwait(false);
        var activeChild = children.FirstOrDefault(c => c.Status == BranchStatus.Active);
        return new BranchState(path, self, children, activeChild);
    }

    public async Task<BranchStartBlock> CheckStartAsync(string parentId, string forkMessageId, CancellationToken ct = default) =>
        (await CheckStartCoreAsync(parentId, forkMessageId, ct).ConfigureAwait(false)).Block;

    public async Task<Conversation> StartAsync(string parentId, string forkMessageId, string? quote, CancellationToken ct = default)
    {
        var check = await CheckStartCoreAsync(parentId, forkMessageId, ct).ConfigureAwait(false);
        if (check.Block != BranchStartBlock.None)
            throw new InvalidOperationException(BranchMessages.For(check.Block));
        var fork = check.Fork ?? throw new InvalidOperationException(BranchMessages.MessageNotFoundMessage);

        // 1. 建立支線對話（標題：有選取的文字用它，沒有就是「支線」，D12）。步驟之間中斷留下的孤兒，啟動時清掉（E10）
        var normalizedQuote = BranchBlocks.NormalizeQuote(quote);
        var conversation = await conversations.CreateAsync(BranchBlocks.TitleFromQuote(quote), ConversationKind.Branch, ct).ConfigureAwait(false);

        // 2. system 與工具快照從上一層複製（E2）：支線的前綴才會和上一層的最後一次請求逐字相同
        var system = await conversations.GetSystemPromptAsync(parentId, ct).ConfigureAwait(false);
        if (system is not null)
            await conversations.SetSystemPromptAsync(conversation.Id, system, ct).ConfigureAwait(false);
        var snapshot = await conversations.GetToolSnapshotAsync(parentId, ct).ConfigureAwait(false);
        if (snapshot is not null)
            await conversations.SetToolSnapshotAsync(conversation.Id, snapshot, ct).ConfigureAwait(false);

        // 3. 支線的列：RootId 是最上層的主線，深度是上一層加一
        var parentDepth = check.ParentBranch?.Depth ?? 0;
        var rootId = check.ParentBranch?.RootId ?? parentId;
        var branch = new Branch(
            conversation.Id, parentId, rootId, parentDepth + 1,
            fork.Id, BranchBlocks.ExcerptOf(fork, BranchLimits.ForkExcerptChars), fork.CreatedAt,
            normalizedQuote, BranchStatus.Active, null, null, time.GetUtcNow(), null);
        await branches.AddAsync(branch, ct).ConfigureAwait(false);

        // 4. 上一層加入的檔案與資料夾複製進支線、標成已送出（E9，同排程的寫法）
        await CopyScopeAsync(parentId, conversation.Id, ct).ConfigureAwait(false);

        Changed?.Invoke(this, new BranchChangedArgs(conversation.Id, parentId, rootId, BranchStatus.Active));
        return conversation;
    }

    public async Task<BranchSummaryDraft> DraftSummaryAsync(string conversationId, CancellationToken ct = default)
    {
        // 4.5：支線要是進行中、要有訊息；不是支線時也當成失敗回傳，不丟例外
        var branch = await branches.GetAsync(conversationId, ct).ConfigureAwait(false);
        if (branch is null)
            return new BranchSummaryDraft(null, new ChatFailure(ChatFailureKind.Unknown, BranchMessages.NotBranchableMessage, Retryable: false));
        if (branch.Status != BranchStatus.Active)
            return new BranchSummaryDraft(null, new ChatFailure(ChatFailureKind.Unknown, BranchMessages.FinishedBranchMessage, Retryable: false));

        var messages = await conversations.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        if (messages.Count == 0)
            return new BranchSummaryDraft(null, new ChatFailure(ChatFailureKind.Unknown, BranchMessages.BranchEmptyMessage, Retryable: false));

        // 上一層的背景：分岔那則回答（找不到就用存好的摘錄）與上一層最後一則使用者訊息
        var parentMessages = await conversations.GetMessagesAsync(branch.ParentId, ct).ConfigureAwait(false);
        var fork = parentMessages.FirstOrDefault(m => m.Id == branch.ForkMessageId);
        var forkText = fork is null ? branch.ForkExcerpt : BranchBlocks.ExcerptOf(fork, BranchLimits.MainContextForkChars);
        var lastUser = parentMessages.LastOrDefault(m => m.Role == ChatRole.User);
        var mainText = lastUser is null ? null : NullIfEmpty(BranchBlocks.ExcerptOf(lastUser, BranchLimits.MainContextUserChars));

        // 已帶回、還沒交出的下一層摘要
        var childSummaries = new List<(string Title, string Summary)>();
        foreach (var child in await branches.ListUndeliveredAsync(conversationId, ct).ConfigureAwait(false))
        {
            var title = (await conversations.GetConversationAsync(child.ConversationId, ct).ConfigureAwait(false))?.Title ?? BranchLimits.DefaultTitle;
            childSummaries.Add((title, child.Summary ?? ""));
        }

        var request = new BranchSummaryRequest(branch.RootId, forkText, branch.Quote, mainText, messages, childSummaries);
        return await summarizer.SummarizeAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<Conversation> MergeAsync(string conversationId, string summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var text = summary.Trim();
        if (text.Length == 0)
            throw new ArgumentException("摘要不能是空的。", nameof(summary));
        if (text.Length > BranchLimits.SummaryMaxChars)
            throw new ArgumentException($"摘要最多 {BranchLimits.SummaryMaxChars} 字。", nameof(summary));

        var branch = await ActiveBranchAsync(conversationId, ct).ConfigureAwait(false);
        if (!await branches.MergeAsync(conversationId, text, time.GetUtcNow(), ct).ConfigureAwait(false))
            throw new InvalidOperationException(BranchMessages.FinishedBranchMessage);

        Changed?.Invoke(this, new BranchChangedArgs(conversationId, branch.ParentId, branch.RootId, BranchStatus.Merged));
        return await ParentAsync(branch, ct).ConfigureAwait(false);
    }

    public async Task<Conversation> AbandonAsync(string conversationId, CancellationToken ct = default)
    {
        var branch = await ActiveBranchAsync(conversationId, ct).ConfigureAwait(false);
        if (!await branches.AbandonAsync(conversationId, time.GetUtcNow(), ct).ConfigureAwait(false))
            throw new InvalidOperationException(BranchMessages.FinishedBranchMessage);

        Changed?.Invoke(this, new BranchChangedArgs(conversationId, branch.ParentId, branch.RootId, BranchStatus.Abandoned));
        return await ParentAsync(branch, ct).ConfigureAwait(false);
    }

    public async Task<Conversation?> FindActiveLeafAsync(string conversationId, CancellationToken ct = default)
    {
        // 最多往下走 MaxDepth 層，每一層最多一條進行中的支線
        Conversation? leaf = null;
        var current = conversationId;
        for (var level = 0; level < BranchLimits.MaxDepth; level++)
        {
            var active = (await branches.ListChildrenAsync(current, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Status == BranchStatus.Active);
            if (active is null)
                break;
            var conversation = await conversations.GetConversationAsync(active.ConversationId, ct).ConfigureAwait(false);
            if (conversation is null)
                break;
            leaf = conversation;
            current = active.ConversationId;
        }
        return leaf;
    }

    public async Task CleanupOrphansAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var id in await branches.ListOrphanConversationsAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await conversations.DeleteAsync(id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("刪除沒有完成的支線失敗（{Type}）", ex.GetType().Name);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("讀取沒有完成的支線失敗（{Type}）", ex.GetType().Name);
        }
    }

    /// <summary>開支線的檢查（4.4 的表，依序）；開支線與 <see cref="CheckStartAsync"/> 共用。通過時 <see cref="StartCheck.Block"/> 是 None。</summary>
    private async Task<StartCheck> CheckStartCoreAsync(string parentId, string forkMessageId, CancellationToken ct)
    {
        var parent = await conversations.GetConversationAsync(parentId, ct).ConfigureAwait(false);
        if (parent is null || parent.Kind is not (ConversationKind.Chat or ConversationKind.Branch))
            return StartCheck.Blocked(BranchStartBlock.NotBranchable);
        if (await sprite.GetIdAsync(ct).ConfigureAwait(false) == parentId)
            return StartCheck.Blocked(BranchStartBlock.NotBranchable);

        var parentBranch = await branches.GetAsync(parentId, ct).ConfigureAwait(false);
        if (parentBranch is { Status: not BranchStatus.Active })
            return StartCheck.Blocked(BranchStartBlock.Finished);
        if ((parentBranch?.Depth ?? 0) >= BranchLimits.MaxDepth)
            return StartCheck.Blocked(BranchStartBlock.Depth);
        if ((await branches.ListChildrenAsync(parentId, ct).ConfigureAwait(false)).Any(c => c.Status == BranchStatus.Active))
            return StartCheck.Blocked(BranchStartBlock.ActiveChild);

        // 分岔的那則必須是上一層裡有文字的助理訊息
        var fork = (await conversations.GetMessagesAsync(parentId, ct).ConfigureAwait(false)).FirstOrDefault(m => m.Id == forkMessageId);
        if (fork is not { Role: ChatRole.Assistant } || BranchBlocks.ExcerptOf(fork, 1).Length == 0)
            return StartCheck.Blocked(BranchStartBlock.MessageNotFound);

        return new StartCheck(BranchStartBlock.None, parentBranch, fork);
    }

    /// <summary>上一層加入的範圍複製進支線。失敗只記 log，不影響開支線（支線的對話與列已經建立）。</summary>
    private async Task CopyScopeAsync(string parentId, string branchId, CancellationToken ct)
    {
        try
        {
            var items = await contexts.GetAsync(parentId, ct).ConfigureAwait(false);
            if (items.Count == 0)
                return;
            var ids = new List<string>(items.Count);
            foreach (var item in items)
                ids.Add((await contexts.AddAsync(branchId, item.Kind, item.Path, item.DisplayName, ct).ConfigureAwait(false)).Id);
            // 標成已送出，但用的是支線自己的識別字當記號（沒有外鍵，同排程的寫法）
            await contexts.MarkEmbeddedAsync(branchId, ids, $"branch-scope:{branchId}", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("複製支線的範圍失敗（{Type}）", ex.GetType().Name);
        }
    }

    /// <summary>帶回、放棄共用：這則要是進行中的支線，而且底下沒有進行中的下一層（帶回或放棄前要先處理下一層）。</summary>
    private async Task<Branch> ActiveBranchAsync(string conversationId, CancellationToken ct)
    {
        var branch = await branches.GetAsync(conversationId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException(NotABranchMessage);
        if (branch.Status != BranchStatus.Active)
            throw new InvalidOperationException(BranchMessages.FinishedBranchMessage);
        if ((await branches.ListChildrenAsync(conversationId, ct).ConfigureAwait(false)).Any(c => c.Status == BranchStatus.Active))
            throw new InvalidOperationException(BranchMessages.ActiveChildMessage);
        return branch;
    }

    private async Task<Conversation> ParentAsync(Branch branch, CancellationToken ct) =>
        await conversations.GetConversationAsync(branch.ParentId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException("找不到上一層的對話。");

    /// <summary>對話的標題；讀不到（或不存在）是空字串。</summary>
    private async Task<string> TitleOfAsync(string conversationId, CancellationToken ct) =>
        (await conversations.GetConversationAsync(conversationId, ct).ConfigureAwait(false))?.Title ?? "";

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>開支線檢查的結果：不能開時 <see cref="Block"/> 是原因；能開時 <see cref="Fork"/> 是分岔的那則。</summary>
    private sealed record StartCheck(BranchStartBlock Block, Branch? ParentBranch = null, ChatMessage? Fork = null)
    {
        public static StartCheck Blocked(BranchStartBlock block) => new(block);
    }
}
