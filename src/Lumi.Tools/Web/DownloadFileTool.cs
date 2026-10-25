using System.ComponentModel;
using Lumi.Core.Tools;
using Lumi.Core.Web;

namespace Lumi.Tools.Web;

public sealed record DownloadFileArgs(
    [property: Description("要下載的檔案網址（http 或 https）")] string Url,
    [property: Description("存放位置：相對於使用者加入的資料夾（例如 下載/報告.pdf），或範圍內的絕對路徑；以 / 結尾或是既有資料夾時，檔名用網址的最後一段")] string Path);

/// <summary>把網址的檔案下載到使用者加入的資料夾（D13：不覆蓋、不下載執行檔與安裝檔）。</summary>
public sealed class DownloadFileTool(IWebClient web) : ToolBase<DownloadFileArgs>
{
    /// <summary>不下載的副檔名（不分大小寫）。</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".sh", ".command", ".app", ".pkg", ".dmg",
    };

    public override ToolDescriptor Descriptor { get; } = new(
        "download_file", "下載檔案",
        "把 http/https 網址的檔案下載到使用者加入的資料夾或檔案範圍內。不會覆蓋既有檔案；單檔最多 200 MB；不能下載執行檔與安裝檔。" +
        "只有使用者要求下載或存檔時才呼叫；下載前會請使用者確認。只想讀內容請用 web_fetch。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(300),
        RequiresScope: true, Group: ToolGroups.Web, Summary: "把網址的檔案下載到你加入的資料夾。");

    protected override ToolPreview Describe(DownloadFileArgs args, ToolContext context)
    {
        if (!WebToolSupport.TryParseUrl(args.Url, out var url, out _))
            return ToolPreview.None;
        return FileWriteSupport.SafePreview(() =>
        {
            var display = TargetDisplay(args.Path, url, context.Scope);
            var detail = display is null ? $"從 {url.Host} 下載" : $"從 {url.Host} 下載到 {display}";
            return new ToolPreview(display, detail, url);
        }, args.Path);
    }

    protected override async Task<ToolResult> InvokeAsync(DownloadFileArgs args, ToolContext context, CancellationToken ct)
    {
        if (!WebToolSupport.TryParseUrl(args.Url, out var url, out var error))
            return ToolResult.Error(error, "網址不正確");
        if (string.IsNullOrWhiteSpace(args.Path))
            return ToolResult.Error("缺少 path（存放位置）。", "參數不正確");
        if (!TryTarget(args.Path, url, context.Scope, out var target, out var targetError))
            return ToolResult.Error(targetError, "參數不正確");

        var resolved = context.Scope.Resolve(target);
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法寫入");

        var full = resolved.FullPath!;
        var display = context.Scope.ToDisplayPath(full);
        var extension = Path.GetExtension(full);
        if (BlockedExtensions.Contains(extension))
            return ToolResult.Error($"不能下載執行檔或安裝檔（{extension}）。", "副檔名不允許");
        if (Directory.Exists(full))
            return ToolResult.Error($"「{display}」是資料夾，不能寫入。", "是資料夾");
        if (File.Exists(full))
            return ToolResult.Error($"「{display}」已經存在，不會覆蓋。請換一個檔名。", "檔案已存在");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var result = await web.DownloadAsync(url, full, WebLimits.DownloadMaxBytes, ct);
            var size = TextFiles.FormatSize(result.Bytes);
            return ToolResult.Ok($"已下載到 {display}（{size}）。", size);
        }
        catch (WebRequestException e)
        {
            return WebToolSupport.Failure(e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"無法寫入：{display}", "無法寫入");
        }
    }

    /// <summary>
    /// 決定實際的存放路徑：path 以 / 或 \ 結尾，或解析後是既有資料夾時，檔名接上網址的最後一段。
    /// 失敗（網址沒有檔名）時 <paramref name="error"/> 有說明。
    /// </summary>
    private static bool TryTarget(string rawPath, Uri url, IFileScope scope, out string target, out string error)
    {
        target = rawPath.Trim();
        error = "";
        var isFolder = target.EndsWith('/') || target.EndsWith('\\');
        if (!isFolder)
        {
            var resolved = scope.Resolve(target);
            isFolder = resolved.IsAllowed && Directory.Exists(resolved.FullPath!);
        }
        if (!isFolder)
            return true;

        var name = FileNameOf(url);
        if (name is null)
        {
            error = "網址沒有檔名，請在 path 指定檔名。";
            return false;
        }
        var folder = target.TrimEnd('/', '\\');
        target = folder.Length == 0 ? name : folder + Path.DirectorySeparatorChar + name;
        return true;
    }

    /// <summary>網址最後一段（已解碼），不能當檔名的字元換成 _；沒有就是 null。</summary>
    private static string? FileNameOf(Uri url)
    {
        var last = Uri.UnescapeDataString(url.Segments[^1]).TrimEnd('/');
        if (last.Length == 0)
            return null;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(last.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    /// <summary>確認卡片的顯示路徑：和執行時相同的規則；解析不了就退回使用者給的字串。</summary>
    private static string? TargetDisplay(string? rawPath, Uri url, IFileScope scope)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;
        if (TryTarget(rawPath, url, scope, out var target, out _))
        {
            var resolved = scope.Resolve(target);
            if (resolved.IsAllowed)
                return scope.ToDisplayPath(resolved.FullPath!);
        }
        return FileWriteSupport.Display(scope, rawPath);
    }
}
