using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserSelectArgs(
    [property: Description("下拉選單的元素，最近一次快照裡 [ref=…] 的內容，例如 e12")] string Ref,
    [property: Description("要選的選項（value 或顯示文字），多選時給多個")] IReadOnlyList<string> Values);

/// <summary>在下拉選單選擇選項（M18）。</summary>
public sealed class BrowserSelectTool(IBrowserSessions sessions) : ToolBase<BrowserSelectArgs>
{
    private const int MaxDetailLength = 100;

    public override ToolDescriptor Descriptor { get; } = new(
        "browser_select", "選擇選項",
        "在下拉選單選擇選項。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(60),
        Group: ToolGroups.Browser, Summary: "在網頁的下拉選單選擇選項。");

    protected override ToolPreview Describe(BrowserSelectArgs args, ToolContext context)
    {
        var joined = string.Join("、", args.Values ?? []);
        var detail = joined.Length <= MaxDetailLength ? joined : joined[..(MaxDetailLength - 1)] + "…";
        return BrowserToolSupport.Preview(sessions, context.ConversationId, args.Ref, $"選擇 {detail}");
    }

    protected override async Task<ToolResult> InvokeAsync(BrowserSelectArgs args, ToolContext context, CancellationToken ct)
    {
        if (!BrowserToolSupport.TryParseRef(args.Ref, out var @ref, out var error))
            return ToolResult.Error(error, "參數不正確");
        if (args.Values is null || args.Values.Count == 0)
            return ToolResult.Error("缺少 values（要選的選項）。", "參數不正確");
        try
        {
            var outcome = await sessions.SelectAsync(context.ConversationId, @ref, args.Values, ct);
            return BrowserToolSupport.FromOutcome(outcome, "已選擇");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
