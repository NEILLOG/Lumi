using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserScrollArgs(
    [property: Description("up、down（預設）、top 或 bottom")] string? Direction = null,
    [property: Description("有值時改成捲動到這個元素（ref，例如 e12）")] string? Ref = null);

/// <summary>捲動目前分頁；有 ref 時捲動到該元素（M18 18G）。</summary>
public sealed class BrowserScrollTool(IBrowserSessions sessions) : ToolBase<BrowserScrollArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_scroll", "捲動網頁",
        "捲動目前網頁。預設往下捲一個畫面；direction 可以是 up、down、top 或 bottom；指定 ref 時改成捲動到那個元素。捲動後會回傳新的快照。",
        ToolPermission.ReadOnly, ToolModes.Workbench, TimeSpan.FromSeconds(30), Group: ToolGroups.Browser, Summary: "捲動網頁。");

    protected override ToolPreview Describe(BrowserScrollArgs args, ToolContext context) =>
        string.IsNullOrWhiteSpace(args.Ref) ? ToolPreview.None : BrowserToolSupport.Preview(sessions, context.ConversationId, args.Ref);

    protected override async Task<ToolResult> InvokeAsync(BrowserScrollArgs args, ToolContext context, CancellationToken ct)
    {
        if (!BrowserRules.TryParseDirection(args.Direction, out var direction, out var directionError))
            return ToolResult.Error(directionError, "參數不正確");

        string? @ref = null;
        if (!string.IsNullOrWhiteSpace(args.Ref))
        {
            if (!BrowserToolSupport.TryParseRef(args.Ref, out var parsed, out var refError))
                return ToolResult.Error(refError, "參數不正確");
            @ref = parsed;
        }

        try
        {
            var outcome = await sessions.ScrollAsync(context.ConversationId, direction, @ref, ct);
            return BrowserToolSupport.FromOutcome(outcome, "已捲動");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
