using Lumi.Core.Branches;
using Lumi.Core.Chat;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 支線服務的假實作（M36）：<see cref="Calls"/> 記下「方法名:參數」；<see cref="States"/> 設定 <see cref="GetStateAsync"/> 的回傳
/// （沒有設定時回「只有自己一節、不是支線」）；<see cref="Drafts"/> 依支線 id 排好草稿；可以用 <see cref="RaiseChanged"/> 手動發事件。
/// </summary>
internal sealed class FakeBranchService : IBranchService
{
    private readonly object _lock = new();
    private readonly List<string> _calls = [];

    public event EventHandler<BranchChangedArgs>? Changed;

    /// <summary>對話 id → <see cref="GetStateAsync"/> 的回傳。</summary>
    public Dictionary<string, BranchState> States { get; } = new(StringComparer.Ordinal);

    /// <summary><see cref="CheckStartAsync"/> 的回傳。</summary>
    public BranchStartBlock StartBlock { get; set; } = BranchStartBlock.None;

    /// <summary><see cref="StartAsync"/> 回傳的對話；null 時建一則新的 <see cref="ConversationKind.Branch"/> 對話。</summary>
    public Conversation? StartResult { get; set; }

    /// <summary>不是 null 時 <see cref="StartAsync"/> 丟這個例外。</summary>
    public Exception? ThrowOnStart { get; set; }

    /// <summary>支線 id → <see cref="DraftSummaryAsync"/> 依序回傳的草稿；用完或沒有設定時回 <c>摘要:{id}</c>。</summary>
    public Dictionary<string, Queue<BranchSummaryDraft>> Drafts { get; } = new(StringComparer.Ordinal);

    /// <summary>支線 id → <see cref="MergeAsync"/>／<see cref="AbandonAsync"/> 回傳的上一層。沒有設定時回一則標題是「上一層」的新對話。</summary>
    public Dictionary<string, Conversation> Parents { get; } = new(StringComparer.Ordinal);

    /// <summary>不是 null 時 <see cref="MergeAsync"/> 丟這個例外。</summary>
    public Exception? ThrowOnMerge { get; set; }

    /// <summary>對話 id → <see cref="FindActiveLeafAsync"/> 的回傳。</summary>
    public Dictionary<string, Conversation> ActiveLeaves { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Calls
    {
        get { lock (_lock) return _calls.ToArray(); }
    }

    private void Record(string call)
    {
        lock (_lock)
            _calls.Add(call);
    }

    public void RaiseChanged(BranchChangedArgs args) => Changed?.Invoke(this, args);

    public Task<BranchState> GetStateAsync(string conversationId, CancellationToken ct = default)
    {
        Record($"GetState:{conversationId}");
        return Task.FromResult(States.GetValueOrDefault(conversationId)
            ?? new BranchState([new BranchPathNode(conversationId, "", 0, null)], null, [], null));
    }

    public Task<BranchStartBlock> CheckStartAsync(string parentId, string forkMessageId, CancellationToken ct = default)
    {
        Record($"CheckStart:{parentId}:{forkMessageId}");
        return Task.FromResult(StartBlock);
    }

    public Task<Conversation> StartAsync(string parentId, string forkMessageId, string? quote, CancellationToken ct = default)
    {
        Record($"Start:{parentId}:{forkMessageId}:{quote ?? "null"}");
        if (ThrowOnStart is not null)
            throw ThrowOnStart;
        var now = DateTimeOffset.UnixEpoch;
        return Task.FromResult(StartResult ?? new Conversation(Conversation.NewId(), quote ?? "支線", now, now, ConversationKind.Branch));
    }

    public Task<BranchSummaryDraft> DraftSummaryAsync(string conversationId, CancellationToken ct = default)
    {
        Record($"Draft:{conversationId}");
        if (Drafts.TryGetValue(conversationId, out var queue) && queue.Count > 0)
            return Task.FromResult(queue.Dequeue());
        return Task.FromResult(new BranchSummaryDraft($"摘要:{conversationId}", null));
    }

    public Task<Conversation> MergeAsync(string conversationId, string summary, CancellationToken ct = default)
    {
        Record($"Merge:{conversationId}:{summary}");
        if (ThrowOnMerge is not null)
            throw ThrowOnMerge;
        return Task.FromResult(ParentOf(conversationId));
    }

    public Task<Conversation> AbandonAsync(string conversationId, CancellationToken ct = default)
    {
        Record($"Abandon:{conversationId}");
        return Task.FromResult(ParentOf(conversationId));
    }

    public Task<Conversation?> FindActiveLeafAsync(string conversationId, CancellationToken ct = default)
    {
        Record($"FindActiveLeaf:{conversationId}");
        return Task.FromResult(ActiveLeaves.GetValueOrDefault(conversationId));
    }

    public Task CleanupOrphansAsync(CancellationToken ct = default)
    {
        Record("CleanupOrphans");
        return Task.CompletedTask;
    }

    private Conversation ParentOf(string conversationId)
    {
        if (Parents.TryGetValue(conversationId, out var parent))
            return parent;
        var now = DateTimeOffset.UnixEpoch;
        return new Conversation(Conversation.NewId(), "上一層", now, now, ConversationKind.Chat);
    }
}
