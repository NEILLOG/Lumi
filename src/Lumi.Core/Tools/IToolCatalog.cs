namespace Lumi.Core.Tools;

/// <summary>設定頁用：一個群組和它的工具。</summary>
public sealed record ToolCatalogGroup(ToolGroupInfo Group, IReadOnlyList<ITool> Tools);

/// <summary>
/// 設定頁與對話迴圈共用的工具目錄：內建工具（M16 起加上 MCP 工具）加上使用者的啟用設定。
/// 工具清單在一則對話內固定（M15 D1）：新對話第一次送出時才呼叫 <see cref="GetEnabledForNewConversationAsync"/>。
/// </summary>
public interface IToolCatalog
{
    /// <summary>新對話第一次送出時要凍結的工具：已啟用的（<c>CanDisable=false</c> 的一律算啟用），依名稱排序。</summary>
    Task<IReadOnlyList<ITool>> GetEnabledForNewConversationAsync(CancellationToken ct);

    /// <summary>全部內建工具（含已關閉），依名稱排序。舊對話補快照用（M15 D3）。</summary>
    IReadOnlyList<ITool> GetAll();

    /// <summary>依名稱找目前能執行的工具；找不到回 null。</summary>
    ITool? Find(string name);

    /// <summary>設定頁用：依群組排好（群組依 Order，組內依 DisplayName 序數排序）。沒有工具的內建群組不回傳；已啟用的 MCP 伺服器即使還沒有工具也會有一個空群組（M16，畫面要顯示說明）。</summary>
    IReadOnlyList<ToolCatalogGroup> ListGroups();

    /// <summary>這個工具能不能設「永遠允許」：Write 或 Network，且 <see cref="ToolDescriptor.CanAlwaysAllow"/>。</summary>
    static bool CanAlwaysAllow(ToolDescriptor d) =>
        d.Permission is ToolPermission.Write or ToolPermission.Network && d.CanAlwaysAllow;
}
