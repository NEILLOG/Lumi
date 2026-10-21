using System.Collections.Concurrent;
using Lumi.App.ViewModels;
using Lumi.Core.Tools;

namespace Lumi.App.Services;

/// <summary>
/// 工作台的權限確認：對話迴圈在 <see cref="RequestAsync"/> 等，卡片上的按鈕呼叫 <see cref="Resolve"/>。
/// 兩邊誰先到都可以（以 call id 對應，按鈕比請求早到也不會遺失）；取消（使用者按停止）會結束等待。
/// </summary>
public sealed class ToolApprovalService : IToolApprovalService, IToolApprovalResponder
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pending = new(StringComparer.Ordinal);

    public async Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        var completion = Get(request.CallId);
        using var registration = ct.Register(static state =>
        {
            var (source, token) = ((TaskCompletionSource<ApprovalDecision>, CancellationToken))state!;
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

    public void Resolve(string callId, ApprovalDecision decision)
    {
        Get(callId).TrySetResult(decision);
        Resolved?.Invoke(this, new ApprovalResolved(callId, decision));
    }

    private TaskCompletionSource<ApprovalDecision> Get(string callId) =>
        _pending.GetOrAdd(callId, _ => new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
}
