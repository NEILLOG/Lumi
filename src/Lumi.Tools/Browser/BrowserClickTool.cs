using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserClickArgs(
    [property: Description("要點擊的元素，最近一次快照裡 [ref=…] 的內容，例如 e12")] string Ref);

/// <summary>點擊目前網頁上的一個元素，回傳點擊後的新快照（M18 D4）。</summary>
public sealed class BrowserClickTool(IBrowserSessions sessions) : ToolBase<BrowserClickArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_click", "點擊",
        "點擊目前網頁上的一個元素（按鈕、連結、核取方塊…），並回傳點擊後的新快照。ref 要用最近一次快照裡的。點擊可能送出表單或改變網站上的資料，只有使用者的要求需要時才點擊。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(60),
        Group: ToolGroups.Browser, Summary: "點擊網頁上的按鈕或連結。");

    protected override ToolPreview Describe(BrowserClickArgs args, ToolContext context) =>
        BrowserToolSupport.Preview(sessions, context.ConversationId, args.Ref);

    protected override async Task<ToolResult> InvokeAsync(BrowserClickArgs args, ToolContext context, CancellationToken ct)
    {
        if (!BrowserToolSupport.TryParseRef(args.Ref, out var @ref, out var error))
            return ToolResult.Error(error, "參數不正確");
        try
        {
            var outcome = await sessions.ClickAsync(context.ConversationId, @ref, ct);
            return BrowserToolSupport.FromOutcome(outcome, "已點擊");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
