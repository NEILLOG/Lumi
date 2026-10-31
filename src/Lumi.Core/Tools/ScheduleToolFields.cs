using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumi.Core.Tools;

/// <summary>
/// 建立排程工具（<c>create_scheduled_prompt</c>，M24 4.4）的參數欄位名稱，供 Core 與工具共用。
/// 模型自己填的 <see cref="GrantedPermissions"/> 不可信：只有確認卡回傳的修改後參數才能決定授權，
/// 沒有修改後參數時 <see cref="RemoveGrantedPermissions"/> 把它拿掉（見 <c>ConversationService</c> 的權限段落）。
/// </summary>
public static class ScheduleToolFields
{
    /// <summary>授權勾選（寫入、網路、破壞性）的欄位名稱。</summary>
    public const string GrantedPermissions = "granted_permissions";

    /// <summary>移除參數 JSON 裡的 <see cref="GrantedPermissions"/>；不是 JSON 物件就原樣回傳（工具自己會回錯誤）。</summary>
    public static string RemoveGrantedPermissions(string argumentsJson)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(argumentsJson) as JsonObject;
        }
        catch (JsonException)
        {
            return argumentsJson;
        }
        if (obj is null)
            return argumentsJson;
        if (!obj.Remove(GrantedPermissions))
            return argumentsJson;
        return obj.ToJsonString();
    }
}
