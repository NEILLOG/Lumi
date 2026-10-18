namespace Lumi.Core.Tools;

public enum ApprovalDecision { AllowOnce, AllowForConversation, Deny }

public sealed record ToolApprovalRequest(
    string CallId,
    string ToolName,
    string DisplayName,
    ToolPermission Permission,
    ToolPreview Preview,
    string ArgumentsJson);

/// <summary>
/// 向使用者請求同意。對話迴圈在 <see cref="RequestAsync"/> 暫停，直到使用者選擇或被取消；
/// 實作在 App（卡片內的確認區）。
/// </summary>
public interface IToolApprovalService
{
    Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct);
}

/// <summary>沒有任何畫面可以詢問時的預設：一律拒絕（安全的預設）。</summary>
public sealed class DenyAllToolApprovalService : IToolApprovalService
{
    public Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct) =>
        Task.FromResult(ApprovalDecision.Deny);
}

/// <summary>
/// 「本次對話都允許」的記憶：只存在記憶體，每則對話各自一份；重開程式就消失，不寫進設定檔或資料庫。
/// <see cref="ToolPermission.Destructive"/> 沒有記住選項，永遠詢問。
/// </summary>
public sealed class ToolPermissionMemory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<ToolPermission>> _allowed = new(StringComparer.Ordinal);

    public bool IsAllowed(string conversationId, ToolPermission permission)
    {
        if (permission is ToolPermission.ReadOnly)
            return true;
        if (permission is ToolPermission.Destructive)
            return false;

        lock (_lock)
            return _allowed.TryGetValue(conversationId, out var set) && set.Contains(permission);
    }

    public void Allow(string conversationId, ToolPermission permission)
    {
        if (permission is ToolPermission.ReadOnly or ToolPermission.Destructive)
            return;

        lock (_lock)
        {
            if (!_allowed.TryGetValue(conversationId, out var set))
                _allowed[conversationId] = set = [];
            set.Add(permission);
        }
    }
}
