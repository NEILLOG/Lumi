namespace Lumi.Core.Tools;

public enum ApprovalDecision { AllowOnce, AllowForConversation, Deny }

public sealed record ToolApprovalRequest(
    string CallId,
    string ToolName,
    string DisplayName,
    ToolPermission Permission,
    ToolPreview Preview,
    string ArgumentsJson)
{
    /// <summary>確認區額外顯示的警告（M17：沒出現過的網站、搜尋引擎）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>false＝不提供「本次對話都允許」（M17：搜尋引擎網址每次都問）。</summary>
    public bool AllowRemember { get; init; } = true;
}

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
/// 內建工具依權限等級記住（允許一次 <c>Write</c> 工具，同等級的內建工具都允許）；
/// MCP 工具（群組以 <c>mcp:</c> 開頭）依工具名稱記住，不連帶其他工具（M16 D13）。
/// <see cref="ToolPermission.Destructive"/> 沒有記住選項，永遠詢問。
/// 這則對話允許過的網站（M17 D4）也記在這裡。
/// </summary>
public sealed class ToolPermissionMemory
{
    private const string McpGroupPrefix = "mcp:";

    private readonly object _lock = new();
    private readonly Dictionary<string, HashSet<string>> _allowed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _approvedSites = new(StringComparer.Ordinal);

    public bool IsAllowed(string conversationId, ToolPermission permission) =>
        IsAllowedKey(conversationId, LevelKey(permission), permission);

    public bool IsAllowed(string conversationId, ToolDescriptor descriptor) =>
        IsAllowedKey(conversationId, KeyFor(descriptor), descriptor.Permission);

    public void Allow(string conversationId, ToolPermission permission) =>
        AllowKey(conversationId, LevelKey(permission), permission);

    public void Allow(string conversationId, ToolDescriptor descriptor) =>
        AllowKey(conversationId, KeyFor(descriptor), descriptor.Permission);

    /// <summary>記住這則對話允許過的網站（<see cref="Web.WebOrigin.Of"/> 的格式）。</summary>
    public void ApproveSite(string conversationId, string origin)
    {
        lock (_lock)
        {
            if (!_approvedSites.TryGetValue(conversationId, out var set))
                _approvedSites[conversationId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(origin);
        }
    }

    public bool IsSiteApproved(string conversationId, string origin)
    {
        lock (_lock)
            return _approvedSites.TryGetValue(conversationId, out var set) && set.Contains(origin);
    }

    private bool IsAllowedKey(string conversationId, string key, ToolPermission permission)
    {
        if (permission is ToolPermission.ReadOnly)
            return true;
        if (permission is ToolPermission.Destructive)
            return false;

        lock (_lock)
            return _allowed.TryGetValue(conversationId, out var set) && set.Contains(key);
    }

    private void AllowKey(string conversationId, string key, ToolPermission permission)
    {
        if (permission is ToolPermission.ReadOnly or ToolPermission.Destructive)
            return;

        lock (_lock)
        {
            if (!_allowed.TryGetValue(conversationId, out var set))
                _allowed[conversationId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(key);
        }
    }

    private static string LevelKey(ToolPermission permission) => "level:" + permission;

    private static string KeyFor(ToolDescriptor descriptor) =>
        descriptor.Group.StartsWith(McpGroupPrefix, StringComparison.Ordinal)
            ? "tool:" + descriptor.Name
            : LevelKey(descriptor.Permission);

    /// <summary>刪除對話時一併清掉它的記憶（對話不存在了，記憶也沒有用處）。</summary>
    public void Clear(string conversationId)
    {
        lock (_lock)
        {
            _allowed.Remove(conversationId);
            _approvedSites.Remove(conversationId);
        }
    }
}
