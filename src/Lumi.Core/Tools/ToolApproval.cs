namespace Lumi.Core.Tools;

/// <summary><see cref="AllowBySchedule"/>：排程執行時，在建立排程時授權的範圍內（M24）；只有排程的權限分流會回這個值。</summary>
public enum ApprovalDecision { AllowOnce, AllowForConversation, Deny, AllowBySchedule }

/// <summary>
/// 使用者的回答。<paramref name="AmendedArgumentsJson"/> 有值時，工具用它執行而不是模型給的參數（M24：建立排程的確認卡可以改授權勾選）；
/// 存檔的 <c>tool_use</c> 仍是模型原本的參數（歷史不能改，agent.md 第 7 節）。
/// </summary>
public sealed record ApprovalAnswer(ApprovalDecision Decision, string? AmendedArgumentsJson = null)
{
    public static implicit operator ApprovalAnswer(ApprovalDecision decision) => new(decision);
}

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

    /// <summary>這次呼叫所屬的對話（M24：排程執行的權限要依對話分流）。</summary>
    public string? ConversationId { get; init; }

    /// <summary>建立排程的確認內容（M24）；不是建立排程的工具時是 null。</summary>
    public ScheduleProposal? Schedule => Preview.Schedule;

    /// <summary>儲存技能的確認內容（M29）；不是 <c>save_skill</c> 時是 null。</summary>
    public SkillProposal? Skill => Preview.Skill;

    /// <summary>執行指令的確認內容（M31）；不是 <c>run_command</c> 時是 null。</summary>
    public Commands.CommandProposal? Command => Preview.Command;
}

/// <summary>
/// 建立排程時確認卡要顯示的內容（M24 4.4）。由 <c>create_scheduled_prompt</c> 的 <c>Describe</c> 產生，放在 <see cref="ToolPreview.Schedule"/>。
/// <paramref name="SuggestedWrite"/>、<paramref name="SuggestedNetwork"/> 是模型建議的預先勾選；破壞性不會被預先勾選。
/// </summary>
public sealed record ScheduleProposal(
    string Name,
    string WhenText,
    string? NextRunText,
    string Prompt,
    string ModelText,
    IReadOnlyList<string> ScopeNames,
    bool SuggestedWrite,
    bool SuggestedNetwork);

/// <summary>
/// 儲存技能時確認卡要顯示的內容（M29 5.6），由 <c>save_skill</c> 的 <c>Describe</c> 產生，放在 <see cref="ToolPreview.Skill"/>。
/// 都是正規化後、通過 <see cref="Skills.SkillRules"/> 檢查的值；<paramref name="ReplacesExisting"/>＝已經有同名的技能，存了會取代它。
/// 第一版確認卡不能編輯（D8）。
/// </summary>
public sealed record SkillProposal(string Name, string Description, string Body, bool ReplacesExisting);

/// <summary>
/// 向使用者請求同意。對話迴圈在 <see cref="RequestAsync"/> 暫停，直到使用者選擇或被取消；
/// 實作在 App（卡片內的確認區）。
/// </summary>
public interface IToolApprovalService
{
    Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct);

    /// <summary>
    /// 帶回修改後參數的版本（M24）。對話迴圈呼叫這個；預設把 <see cref="RequestAsync"/> 的結果包起來，
    /// 只有需要回傳修改後參數的實作（App 的 <c>ToolApprovalService</c>）才覆寫。
    /// </summary>
    async Task<ApprovalAnswer> RequestAnswerAsync(ToolApprovalRequest request, CancellationToken ct) =>
        new(await RequestAsync(request, ct).ConfigureAwait(false));

    /// <summary>
    /// 不必詢問使用者就能決定時回傳答案（M24：排程授權範圍內）；回 null 才會發出 <see cref="ApprovalRequired"/> 並呼叫 <see cref="RequestAnswerAsync"/>。
    /// 預設 null。對話迴圈先呼叫這個，所以自動允許的呼叫不會讓介面顯示確認按鈕。
    /// </summary>
    ApprovalAnswer? AutoAnswer(ToolApprovalRequest request) => null;
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
/// MCP 工具（群組以 <c>mcp:</c> 開頭）依工具名稱記住，不連帶其他工具（M16 D13）；
/// 瀏覽器工具依「群組＋權限等級」記住（M18 D6），允許一次 <c>browser_click</c> 不會連帶允許 <c>write_file</c>。
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

    private static string KeyFor(ToolDescriptor descriptor)
    {
        if (descriptor.Group.StartsWith(McpGroupPrefix, StringComparison.Ordinal))
            return "tool:" + descriptor.Name;
        if (descriptor.Group == ToolGroups.Browser)
            return "group:" + ToolGroups.Browser + ":" + descriptor.Permission;
        return LevelKey(descriptor.Permission);
    }

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
