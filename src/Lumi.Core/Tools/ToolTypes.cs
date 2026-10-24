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
/// <param name="RequiresMemory">長期記憶的工具（M14）：記憶在設定裡關閉時，工具不能執行（回說明、不詢問權限）；定義仍照送（2026-10-25：工具清單改變會讓 thinking 簽章失效）。只依使用者的設定、不依模式。</param>
public sealed record ToolDescriptor(
    string Name,
    string DisplayName,
    string Description,
    ToolPermission Permission,
    ToolModes Modes,
    TimeSpan Timeout,
    bool RequiresScope = false,
    bool RequiresMemory = false);

/// <summary>顯示用：卡片標題的「對象」，以及確認區顯示的影響說明（路徑、增減行數…）。</summary>
public sealed record ToolPreview(string? Target, string? Detail)
{
    public static readonly ToolPreview None = new(null, null);
}

/// <summary>
/// 工具的回傳。錯誤用 <see cref="Error"/> 回給模型，不要讓例外往外丟（agent.md 第 8 節）。
/// <paramref name="Summary"/> 是卡片標題的「結果摘要」，由工具提供，畫面不猜。
/// </summary>
public sealed record ToolResult(bool IsError, string Content, string? Summary)
{
    public static ToolResult Ok(string content, string? summary = null) => new(false, content, summary);

    public static ToolResult Error(string message, string? summary = null) => new(true, message, summary ?? message);
}

/// <summary>執行工具時的環境。<see cref="Scope"/> 是這則對話的檔案範圍；檔案類工具只能經由它存取檔案系統。</summary>
public sealed record ToolContext(string ConversationId, ChatMode Mode, TimeProvider Time, IFileScope Scope);

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
