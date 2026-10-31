using System.Collections.Concurrent;
using Lumi.App.ViewModels;
using Lumi.Core.Tools;

namespace Lumi.App.Services;

/// <summary>
/// 工作台的權限確認：對話迴圈在 <see cref="RequestAnswerAsync"/> 等，卡片上的按鈕呼叫 <see cref="Resolve(string, ApprovalAnswer)"/>。
/// 兩邊誰先到都可以（以 call id 對應，按鈕比請求早到也不會遺失）；取消（使用者按停止）會結束等待。
/// 回答可以帶修改後的參數（M24：建立排程的授權勾選），等待的一方拿到完整的 <see cref="ApprovalAnswer"/>。
/// </summary>
public sealed class ToolApprovalService : IToolApprovalService, IToolApprovalResponder
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalAnswer>> _pending = new(StringComparer.Ordinal);

    public async Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct) =>
        (await RequestAnswerAsync(request, ct).ConfigureAwait(false)).Decision;

    public async Task<ApprovalAnswer> RequestAnswerAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var completion = Get(request.CallId);
        using var registration = ct.Register(static state =>
        {
            var (source, token) = ((TaskCompletionSource<ApprovalAnswer>, CancellationToken))state!;
            source.TrySetCanceled(token);
        }, (completion, ct));

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(request.CallId, out _);
        }
    }

    public event EventHandler<ApprovalResolved>? Resolved;

    public void Resolve(string callId, ApprovalDecision decision) => Resolve(callId, new ApprovalAnswer(decision));

    public void Resolve(string callId, ApprovalAnswer answer)
    {
        // 先通知另一邊更新畫面，再放行對話迴圈：迴圈接著執行工具、發出完成事件時，卡片已經是「已允許」，
        // 不會被晚到的通知蓋掉完成的狀態
        Resolved?.Invoke(this, new ApprovalResolved(callId, answer.Decision));
        Get(callId).TrySetResult(answer);
    }

    private TaskCompletionSource<ApprovalAnswer> Get(string callId) =>
        _pending.GetOrAdd(callId, _ => new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously));
}
