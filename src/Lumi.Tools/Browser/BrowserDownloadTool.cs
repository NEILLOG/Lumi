using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;
using Lumi.Core.Web;

namespace Lumi.Tools.Browser;

public sealed record BrowserDownloadArgs(
    [property: Description("要點擊的下載連結或按鈕，最近一次快照裡 [ref=…] 的內容，例如 e12")] string Ref,
    [property: Description("存放位置：相對於使用者加入的資料夾（例如 下載/報告.pdf），或範圍內的絕對路徑")] string Path);

/// <summary>點擊網頁上的下載連結，把下載的檔案存進範圍內（M18 D14：不覆蓋、不存執行檔與安裝檔，上限 200 MB）。</summary>
public sealed class BrowserDownloadTool(IBrowserSessions sessions) : ToolBase<BrowserDownloadArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_download", "下載檔案",
        "點擊網頁上的下載連結或按鈕，把下載的檔案存到使用者加入的資料夾（需要登入才能下載的檔案用這個；一般網址直接用 download_file）。" +
        "不會覆蓋既有檔案；單檔最多 200 MB；不能下載執行檔與安裝檔。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(300),
        RequiresScope: true, Group: ToolGroups.Browser, Summary: "點擊網頁上的連結，把下載的檔案存到你加入的資料夾。");

    protected override ToolPreview Describe(BrowserDownloadArgs args, ToolContext context)
    {
        var detail = string.IsNullOrWhiteSpace(args.Path)
            ? null
            : FileWriteSupport.SafeDetail(() => $"下載到 {FileWriteSupport.Display(context.Scope, args.Path)}");
        return BrowserToolSupport.Preview(sessions, context.ConversationId, args.Ref, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(BrowserDownloadArgs args, ToolContext context, CancellationToken ct)
    {
        if (!BrowserToolSupport.TryParseRef(args.Ref, out var @ref, out var refError))
            return ToolResult.Error(refError, "參數不正確");
        if (string.IsNullOrWhiteSpace(args.Path))
            return ToolResult.Error("缺少 path（存放位置）。", "參數不正確");

        var resolved = context.Scope.Resolve(args.Path);
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法寫入");

        // 檔名要等下載完成才知道，所以副檔名檢查兩次：這裡先檢查使用者給的路徑，下載後再檢查實際的檔名
        var full = resolved.FullPath!;
        var display = context.Scope.ToDisplayPath(full);
        var extension = Path.GetExtension(full);
        if (BrowserRules.IsBlockedDownloadExtension(extension))
            return ToolResult.Error($"不能下載執行檔或安裝檔（{extension}）。", "副檔名不允許");
        if (Directory.Exists(full))
            return ToolResult.Error($"「{display}」是資料夾，不能寫入。請在 path 指定檔名。", "是資料夾");
        if (File.Exists(full))
            return ToolResult.Error($"「{display}」已經存在，不會覆蓋。請換一個檔名。", "檔案已存在");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var download = await sessions.DownloadAsync(context.ConversationId, @ref, full, WebLimits.DownloadMaxBytes, ct);

            var actualExtension = Path.GetExtension(download.FileName);
            if (BrowserRules.IsBlockedDownloadExtension(actualExtension))
            {
                TextFileWriter.TryDelete(full);
                return ToolResult.Error($"不能下載執行檔或安裝檔（{actualExtension}）。", "副檔名不允許");
            }

            var size = TextFiles.FormatSize(download.Bytes);
            return ToolResult.Ok($"已下載到 {display}（{size}）。", size);
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"無法寫入：{display}", "無法寫入");
        }
    }
}
