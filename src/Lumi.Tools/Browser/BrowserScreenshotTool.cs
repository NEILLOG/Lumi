using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserScreenshotArgs(
    [property: Description("true＝整頁截圖（高度最多 4000 像素）；預設 false，只截目前視窗")] bool? FullPage = null);

/// <summary>對目前分頁截圖，圖片附在工具結果裡給模型看（M18 D12）。</summary>
public sealed class BrowserScreenshotTool(IBrowserSessions sessions) : ToolBase<BrowserScreenshotArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_screenshot", "網頁截圖",
        "對目前網頁截圖並交給你看。只有快照看不出來的視覺內容（圖表、版面、圖片裡的文字）才需要；一般操作用快照就夠，截圖比較貴。",
        ToolPermission.ReadOnly, ToolModes.Workbench, TimeSpan.FromSeconds(60), Group: ToolGroups.Browser, Summary: "網頁截圖。");

    protected override async Task<ToolResult> InvokeAsync(BrowserScreenshotArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            var shot = await sessions.ScreenshotAsync(context.ConversationId, args.FullPage == true, ct);
            if (shot.Data.LongLength > BrowserLimits.MaxScreenshotBytes)
                return ToolResult.Error("截圖太大（超過 5 MB）。", "內容太大");
            return ToolResult.Ok($"已截圖（{shot.Width}×{shot.Height}），圖片附在這則結果裡。", $"截圖 {shot.Width}×{shot.Height}") with
            {
                Images = [new ToolImage(shot.Data, shot.MediaType, "網頁截圖.jpg")],
            };
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
