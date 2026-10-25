using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

/// <summary>沒有參數。</summary>
public sealed record BrowserBackArgs();

/// <summary>回到瀏覽器的上一頁（M18）。</summary>
public sealed class BrowserBackTool(IBrowserSessions sessions) : ToolBase<BrowserBackArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_back", "回上一頁",
        "回到瀏覽器的上一頁，回傳新的快照。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(60),
        Group: ToolGroups.Browser, Summary: "回到網頁的上一頁。");

    protected override ToolPreview Describe(BrowserBackArgs args, ToolContext context)
    {
        var host = sessions.CurrentUrl(context.ConversationId)?.Host;
        return new ToolPreview(host is null ? "回上一頁" : $"回上一頁 · {host}", null);
    }

    protected override async Task<ToolResult> InvokeAsync(BrowserBackArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            var outcome = await sessions.BackAsync(context.ConversationId, ct);
            return BrowserToolSupport.FromOutcome(outcome, "已回上一頁");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
