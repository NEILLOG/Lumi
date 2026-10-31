using Lumi.Core.Reminders;

namespace Lumi.Core.Tools;

/// <summary>排程執行時的權限判斷結果。</summary>
public enum ScheduledRunVerdict { Allow, Ask }

/// <summary>
/// 排程執行時，工具是否能用建立排程時的授權直接執行（M24 4.3、卡點 5）。純邏輯，沒有相依的服務。
/// 規則：排除類（記憶、不能設永遠允許的工具、排程管理、網址來源警告、不提供記住的網站、一律詢問的工具）一律詢問；
/// 其他依 <see cref="ScheduleGrant.Allows"/>。
/// </summary>
public static class ScheduledRunApprovalPolicy
{
    /// <summary>
    /// 屬於「不管授權多少都要問」的類別。這類工具的「以後都允許」也不寫回排程（路由層會把它降成允許一次）。
    /// </summary>
    public static bool IsExcluded(ToolDescriptor descriptor, ToolApprovalRequest request) =>
        descriptor.RequiresMemory
        || !descriptor.CanAlwaysAllow
        || descriptor.Group == ToolGroups.Reminders
        || request.Warnings.Count > 0
        || !request.AllowRemember
        || descriptor.AlwaysAsk;

    public static ScheduledRunVerdict Decide(ToolDescriptor descriptor, ToolApprovalRequest request, ScheduleGrant grant) =>
        !IsExcluded(descriptor, request) && grant.Allows(request.Permission)
            ? ScheduledRunVerdict.Allow
            : ScheduledRunVerdict.Ask;
}
