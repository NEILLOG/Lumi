using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserSnapshotArgs(
    [property: Description("快照最多幾個字，預設 12000，範圍 2000 到 30000")] int? MaxChars = null);

/// <summary>重新取得目前分頁的快照（M18 18G）。</summary>
public sealed class BrowserSnapshotTool(IBrowserSessions sessions) : ToolBase<BrowserSnapshotArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_snapshot", "查看網頁",
        "重新取得目前網頁的快照（含最新的 ref）。使用者在瀏覽器裡自己操作過、或你懷疑頁面已經變了之後使用。",
        ToolPermission.ReadOnly, ToolModes.Workbench, TimeSpan.FromSeconds(60), Group: ToolGroups.Browser, Summary: "查看目前網頁。");

    protected override async Task<ToolResult> InvokeAsync(BrowserSnapshotArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            var maxChars = BrowserRules.TryParseSnapshotChars(args.MaxChars);
            var snapshot = await sessions.SnapshotAsync(context.ConversationId, maxChars, ct);
            return BrowserToolSupport.FromOutcome(new BrowserOutcome("", snapshot), $"{snapshot.RefCount} 個元素");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
