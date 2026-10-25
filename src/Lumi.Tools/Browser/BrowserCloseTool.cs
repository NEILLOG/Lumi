using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserCloseArgs();

/// <summary>關閉這則對話的瀏覽器分頁；登入狀態仍然保留（M18 18G）。</summary>
public sealed class BrowserCloseTool(IBrowserSessions sessions) : ToolBase<BrowserCloseArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_close", "關閉瀏覽器分頁",
        "關閉這則對話開啟的瀏覽器分頁（登入狀態仍然保留）。做完網頁相關的工作後可以呼叫。",
        ToolPermission.ReadOnly, ToolModes.Workbench, TimeSpan.FromSeconds(30), Group: ToolGroups.Browser, Summary: "關閉這則對話的瀏覽器分頁。");

    protected override async Task<ToolResult> InvokeAsync(BrowserCloseArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            await sessions.CloseAsync(context.ConversationId, ct);
            return ToolResult.Ok("已關閉瀏覽器分頁。", "已關閉");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
