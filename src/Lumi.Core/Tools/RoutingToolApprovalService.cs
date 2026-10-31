using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tools;

/// <summary>
/// DI 註冊的 <see cref="IToolApprovalService"/>（M24 4.3）：排程執行中的對話依排程授權分流，其他對話原樣交給 <paramref name="inner"/>。
/// <list type="bullet">
/// <item>授權範圍內 → <see cref="ApprovalDecision.AllowBySchedule"/>，不詢問。</item>
/// <item>授權範圍外 → 暫停這次排程執行（<see cref="IScheduledRunHost.PauseForApprovalAsync"/>），等使用者在紀錄裡回答。</item>
/// <item>排程的「以後都允許」→ 寫回排程的授權，這次當成允許一次；排除類的工具只當成允許一次。</item>
/// </list>
/// 絕不把 <see cref="ApprovalDecision.AllowForConversation"/> 傳回 <c>ConversationService</c>：排程對話的「本次對話都允許」沒有意義，也不能寫進記憶。
/// </summary>
public sealed class RoutingToolApprovalService(
    IToolApprovalService inner,
    IScheduledRunHost host,
    IToolCatalog catalog,
    ILogger<RoutingToolApprovalService>? logger = null) : IToolApprovalService
{
    private readonly ILogger<RoutingToolApprovalService> _logger = logger ?? NullLogger<RoutingToolApprovalService>.Instance;

    public async Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct) =>
        (await RequestAnswerAsync(request, ct).ConfigureAwait(false)).Decision;

    /// <summary>排程執行中、且在授權範圍內：直接回 <see cref="ApprovalDecision.AllowBySchedule"/>；其他回 null。</summary>
    public ApprovalAnswer? AutoAnswer(ToolApprovalRequest request)
    {
        var conversationId = request.ConversationId;
        var run = conversationId is null ? null : host.FindByConversation(conversationId);
        if (run is null)
            return null;

        // 找不到工具的定義（理論上不會發生）就當成要詢問，不能因此放行
        var descriptor = catalog.Find(request.ToolName)?.Descriptor;
        if (descriptor is null)
        {
            _logger.LogWarning("排程執行中找不到工具 {Tool}，改為詢問。", request.ToolName);
            return null;
        }

        return ScheduledRunApprovalPolicy.Decide(descriptor, request, run.Grant) == ScheduledRunVerdict.Allow
            ? new ApprovalAnswer(ApprovalDecision.AllowBySchedule)
            : null;
    }

    public async Task<ApprovalAnswer> RequestAnswerAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        if (AutoAnswer(request) is { } auto)
            return auto;

        var conversationId = request.ConversationId;
        var run = conversationId is null ? null : host.FindByConversation(conversationId);
        if (conversationId is null || run is null)
            return await inner.RequestAnswerAsync(request, ct).ConfigureAwait(false);

        var descriptor = catalog.Find(request.ToolName)?.Descriptor;
        return await AskWithPauseAsync(conversationId, run, descriptor, request, ct).ConfigureAwait(false);
    }

    private async Task<ApprovalAnswer> AskWithPauseAsync(
        string conversationId, ActiveScheduledRun run, ToolDescriptor? descriptor, ToolApprovalRequest request, CancellationToken ct)
    {
        await using var pause = await host.PauseForApprovalAsync(conversationId, request, ct).ConfigureAwait(false);
        var answer = await inner.RequestAnswerAsync(request, ct).ConfigureAwait(false);

        if (answer.Decision != ApprovalDecision.AllowForConversation)
            return answer;

        // 「以後都允許」：排除類（與找不到定義的工具）不寫回排程，只當成允許一次
        if (descriptor is null || ScheduledRunApprovalPolicy.IsExcluded(descriptor, request))
            return answer with { Decision = ApprovalDecision.AllowOnce };

        await host.AddGrantAsync(run.ScheduleId, request.Permission, ct).ConfigureAwait(false);
        return answer with { Decision = ApprovalDecision.AllowOnce };
    }
}
