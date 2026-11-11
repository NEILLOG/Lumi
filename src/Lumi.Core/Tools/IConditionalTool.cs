namespace Lumi.Core.Tools;

public enum ToolAvailabilityKind { Available, NotOnThisPlatform, Unavailable }

/// <param name="Reason">給使用者看的白話原因；Available 時是 null。</param>
public sealed record ToolAvailability(ToolAvailabilityKind Kind, string? Reason)
{
    public static readonly ToolAvailability Yes = new(ToolAvailabilityKind.Available, null);
}

/// <summary>
/// 能不能用要看這台電腦的工具（例如 Outlook 工具要有 classic Outlook）。每次查詢都要快、不能卡住。
/// <see cref="ToolAvailabilityKind.NotOnThisPlatform"/>：工具清單頁不顯示；<see cref="ToolAvailabilityKind.Unavailable"/>：顯示原因、開關停用。
/// 兩種情況新對話都不帶這個工具的定義。
/// </summary>
public interface IConditionalTool
{
    ToolAvailability CheckAvailability();
}
