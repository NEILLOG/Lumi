using Lumi.Core.Chat;
using Lumi.Core.Context;
using Microsoft.Extensions.Logging;

namespace Lumi.Core.Branches;

/// <summary>支線的開、帶回、放棄與狀態（計畫 4.4～4.6）。TODO(36E)：Phase 0 只有簽章。</summary>
public sealed class BranchService(
    IConversationStore conversations, IBranchStore branches, IConversationContextStore contexts, IBranchSummarizer summarizer,
    SpriteConversationService sprite, TimeProvider time, ILogger<BranchService>? logger = null) : IBranchService
{
    public event EventHandler<BranchChangedArgs>? Changed;

    // TODO(36E)：實作後拿掉。讓 Phase 0 的主建構子參數與事件不觸發「未使用」警告
    internal object Dependencies => (conversations, branches, contexts, summarizer, sprite, time, logger, Changed);

    public Task<BranchState> GetStateAsync(string conversationId, CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
    public Task<BranchStartBlock> CheckStartAsync(string parentId, string forkMessageId, CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
    public Task<Conversation> StartAsync(string parentId, string forkMessageId, string? quote, CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
    public Task<BranchSummaryDraft> DraftSummaryAsync(string conversationId, CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
    public Task<Conversation> MergeAsync(string conversationId, string summary, CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
    public Task<Conversation> AbandonAsync(string conversationId, CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
    public Task<Conversation?> FindActiveLeafAsync(string conversationId, CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
    public Task CleanupOrphansAsync(CancellationToken ct = default) => throw new NotImplementedException("TODO(36E)");
}
