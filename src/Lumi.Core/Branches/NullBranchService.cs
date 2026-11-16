using Lumi.Core.Chat;

namespace Lumi.Core.Branches;

/// <summary>沒有支線功能時的預設：路徑只有自己一節、不能開支線。正式的 <see cref="BranchService"/> 在整合時註冊。</summary>
public sealed class NullBranchService(IConversationStore conversations) : IBranchService
{
    public event EventHandler<BranchChangedArgs>? Changed { add { } remove { } }

    public async Task<BranchState> GetStateAsync(string conversationId, CancellationToken ct = default)
    {
        string title;
        try
        {
            title = (await conversations.GetConversationAsync(conversationId, ct).ConfigureAwait(false))?.Title ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            title = "";
        }
        return new BranchState([new BranchPathNode(conversationId, title, 0, null)], null, [], null);
    }

    public Task<BranchStartBlock> CheckStartAsync(string parentId, string forkMessageId, CancellationToken ct = default) =>
        Task.FromResult(BranchStartBlock.NotBranchable);

    public Task<Conversation> StartAsync(string parentId, string forkMessageId, string? quote, CancellationToken ct = default) =>
        throw new NotSupportedException("支線功能沒有啟用。");

    public Task<BranchSummaryDraft> DraftSummaryAsync(string conversationId, CancellationToken ct = default) =>
        throw new NotSupportedException("支線功能沒有啟用。");

    public Task<Conversation> MergeAsync(string conversationId, string summary, CancellationToken ct = default) =>
        throw new NotSupportedException("支線功能沒有啟用。");

    public Task<Conversation> AbandonAsync(string conversationId, CancellationToken ct = default) =>
        throw new NotSupportedException("支線功能沒有啟用。");

    public Task<Conversation?> FindActiveLeafAsync(string conversationId, CancellationToken ct = default) => Task.FromResult<Conversation?>(null);

    public Task CleanupOrphansAsync(CancellationToken ct = default) => Task.CompletedTask;
}
