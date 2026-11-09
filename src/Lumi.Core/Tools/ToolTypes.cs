using System.Text.Json.Nodes;
using Lumi.Core.Chat;

namespace Lumi.Core.Tools;

/// <summary>權限等級是必填、沒有預設值（agent.md 第 8 節）。<c>Write</c> 以上預設要使用者確認。</summary>
public enum ToolPermission { ReadOnly, Write, Network, Destructive }

[Flags]
public enum ToolModes
{
    Workbench = 1,
    Sprite = 2,
    Both = Workbench | Sprite,
}

public static class ToolModesExtensions
{
    /// <summary>這個工具能不能在 <paramref name="mode"/> 執行。</summary>
    public static bool Allows(this ToolModes modes, ChatMode mode) =>
        modes.HasFlag(mode == ChatMode.Workbench ? ToolModes.Workbench : ToolModes.Sprite);
}

/// <param name="Name">只允許英數、底線與連字號，最長 64（兩家 API 的共同限制）。</param>
/// <param name="DisplayName">繁中動作名稱，例如「讀取檔案」，顯示在工具卡片標題。</param>
/// <param name="Description">給模型看，要寫清楚何時該呼叫這個工具。</param>
/// <param name="Modes">能在哪些模式執行。定義一律送出，模式不符時在執行時回錯誤（M5 的 Q1）。</param>
/// <param name="Timeout">每個工具都要有逾時。</param>
/// <param name="RequiresScope">需要使用者加入的檔案或資料夾才有意義：範圍是空的時，請求不帶這個工具的定義。</param>
/// <param name="Group">工具清單頁的群組代號（<see cref="ToolGroups"/>；MCP 工具用 <c>mcp:{伺服器 Id}</c>）。</param>
/// <param name="Summary">給使用者看的一句話（設定頁），不送給模型。</param>
/// <param name="CanDisable">false＝設定頁不能關（<c>show_details</c>）。</param>
/// <param name="CanAlwaysAllow">false＝不能設「永遠允許」（<c>memory_save</c>）；Destructive 一律不行，不必另外設。</param>
/// <param name="AlwaysAsk">true＝一定詢問，不看設定的「永遠允許」與「本次對話都允許」，確認區也不提供「本次對話都允許」（M24：建立排程）。</param>
/// <param name="RequiresMemory">長期記憶的工具（M14）：記憶在設定裡關閉時，工具不能執行（回說明、不詢問權限）；定義仍照送（2026-10-25：工具清單改變會讓 thinking 簽章失效）。只依使用者的設定、不依模式。</param>
/// <param name="OptInWarning">有值＝預設關閉的工具（M31 D13）：使用者在工具清單頁開啟時要先確認這段文字；設定關掉後立即生效，已經開著的對話也不能再用（D14）。</param>
/// <param name="WarnsAfterExternalContent">true＝這則對話讀過網頁、MCP 等外部內容時，確認卡加上警告（M31 D12）。</param>
public sealed record ToolDescriptor(
    string Name,
    string DisplayName,
    string Description,
    ToolPermission Permission,
    ToolModes Modes,
    TimeSpan Timeout,
    bool RequiresScope = false,
    bool RequiresMemory = false,
    string Group = ToolGroups.General,
    string? Summary = null,
    bool CanDisable = true,
    bool CanAlwaysAllow = true,
    bool AlwaysAsk = false,
    string? OptInWarning = null,
    bool WarnsAfterExternalContent = false);

/// <summary>
/// 顯示用：卡片標題的「對象」，以及確認區顯示的影響說明（路徑、增減行數…）。
/// <paramref name="Url"/>：這次會連到的網址；有值時對話迴圈會套用網址來源規則（M17 D4）。
/// <paramref name="Schedule"/>：建立排程的確認內容（M24），有值時確認區換成排程確認卡。
/// <paramref name="Skill"/>：儲存技能的確認內容（M29），有值時確認區換成技能確認卡。
/// <paramref name="Command"/>：執行指令的確認內容（M31），有值時確認區換成指令確認卡。
/// <paramref name="Rejection"/>：有值＝不詢問、不執行，直接把這段錯誤回給模型（M31 D11：被擋掉的指令）。
/// </summary>
public sealed record ToolPreview(
    string? Target,
    string? Detail,
    Uri? Url = null,
    ScheduleProposal? Schedule = null,
    SkillProposal? Skill = null,
    Commands.CommandProposal? Command = null,
    string? Rejection = null)
{
    public static readonly ToolPreview None = new(null, null);
}

/// <summary>
/// 工具的回傳。錯誤用 <see cref="Error"/> 回給模型，不要讓例外往外丟（agent.md 第 8 節）。
/// <paramref name="Summary"/> 是卡片標題的「結果摘要」，由工具提供，畫面不猜。
/// </summary>
public sealed record ToolResult(bool IsError, string Content, string? Summary)
{
    /// <summary>工具附的圖片（M18 D12，例如網頁截圖）；對話迴圈會存進附件資料夾，不放在 <see cref="Content"/> 裡。</summary>
    public IReadOnlyList<ToolImage> Images { get; init; } = [];

    /// <summary>
    /// 只給畫面顯示、不送給模型的圖片檔（22F：生成或編輯的圖片，完整路徑）。不存進附件資料夾，結果區塊直接記路徑。
    /// </summary>
    public IReadOnlyList<string> ImageFiles { get; init; } = [];

    public static ToolResult Ok(string content, string? summary = null) => new(false, content, summary);

    public static ToolResult Error(string message, string? summary = null) => new(true, message, summary ?? message);
}

/// <summary>工具結果附的圖片（M18 D12）。位元組由對話迴圈存進附件資料夾，結果區塊只記檔名。</summary>
public sealed record ToolImage(byte[] Data, string MediaType, string DisplayName);

/// <summary>執行工具時的環境。<see cref="Scope"/> 是這則對話的檔案範圍；檔案類工具只能經由它存取檔案系統。</summary>
/// <param name="Progress">工具回報進度（顯示在卡片上，不存檔、不送給模型；M19 D12）。沒有畫面時是 null。</param>
public sealed record ToolContext(string ConversationId, ChatMode Mode, TimeProvider Time, IFileScope Scope, IProgress<string>? Progress = null);

public interface ITool
{
    ToolDescriptor Descriptor { get; }

    /// <summary>由引數型別自動產生的 JSON Schema（頂層一律是 object）。</summary>
    JsonNode InputSchema { get; }

    ToolPreview Preview(string argumentsJson, ToolContext context);

    /// <summary>引數是模型給的原始 JSON。引數不正確時回 <see cref="ToolResult.Error"/>，不丟例外。</summary>
    Task<ToolResult> ExecuteAsync(string argumentsJson, ToolContext context, CancellationToken ct);
}

public interface IToolRegistry
{
    /// <summary>
    /// 所有工具（依名稱排序）。請求一律帶全部工具的定義，不依模式過濾：tools 在快取前綴的最前面，
    /// 切換模式時改變工具清單會讓整個 prompt 快取失效（M5 的 Q1）。模式限制在執行時用 <see cref="ToolModesExtensions.Allows"/> 檢查。
    /// </summary>
    IReadOnlyList<ITool> GetAll();

    /// <summary>依名稱找工具（不分模式）；找不到回 null。</summary>
    ITool? Find(string name);
}
