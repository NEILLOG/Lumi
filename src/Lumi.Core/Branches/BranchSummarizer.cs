using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging;

namespace Lumi.Core.Branches;

/// <summary>帶回時整理支線摘要的那一次 AI 呼叫（計畫 4.5，仿 <c>ContextNoteScribe</c>）。TODO(36C)：Phase 0 只有簽章。</summary>
public sealed class BranchSummarizer(IChatProvider provider, IModelCatalog catalog, IUsageRecorder? usage = null, ILogger<BranchSummarizer>? logger = null) : IBranchSummarizer
{
    // TODO(36C)：實作後拿掉。讓 Phase 0 的主建構子參數不觸發「未使用」警告
    internal (IChatProvider, IModelCatalog, IUsageRecorder?, ILogger<BranchSummarizer>?) Dependencies => (provider, catalog, usage, logger);

    public Task<BranchSummaryDraft> SummarizeAsync(BranchSummaryRequest request, CancellationToken ct = default) => throw new NotImplementedException("TODO(36C)");
}
