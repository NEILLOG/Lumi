using System.ComponentModel;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record CopyFileArgs(
    [property: Description("來源檔案路徑：相對於使用者加入的資料夾，或範圍內的絕對路徑")] string SourcePath,
    [property: Description("目的檔案路徑（新的名稱或位置）：相對於使用者加入的資料夾，或範圍內的絕對路徑；目的已存在會失敗")] string DestinationPath);

/// <summary>複製範圍內的一個檔案。目的已存在就失敗，不覆蓋。</summary>
public sealed class CopyFileTool : ToolBase<CopyFileArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "copy_file", "複製檔案",
        "把使用者加入的範圍內的一個檔案複製到另一個位置（可以是同資料夾的新名稱，也可以是別的資料夾）。目的檔已存在時失敗，不會覆蓋。" +
        "只能複製檔案，不能複製資料夾；目的的上層資料夾不存在時會自動建立。不能複製成 .docx、.xlsx、.pptx、.pdf、壓縮檔等副檔名。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromMinutes(5), RequiresScope: true, Group: ToolGroups.FilesWrite, Summary: "複製檔案。");

    protected override ToolPreview Describe(CopyFileArgs args, ToolContext context) =>
        FileWriteSupport.SafePreview(() =>
        {
            var source = string.IsNullOrWhiteSpace(args.SourcePath) ? "" : FileWriteSupport.Display(context.Scope, args.SourcePath);
            var destination = string.IsNullOrWhiteSpace(args.DestinationPath) ? "" : FileWriteSupport.Display(context.Scope, args.DestinationPath);
            return new ToolPreview(source.Length == 0 ? null : source, $"{source} → {destination}");
        }, args.SourcePath);

    protected override Task<ToolResult> InvokeAsync(CopyFileArgs args, ToolContext context, CancellationToken ct) =>
        CopyAsync(args, context, ct);

    private static async Task<ToolResult> CopyAsync(CopyFileArgs args, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.SourcePath) || string.IsNullOrWhiteSpace(args.DestinationPath))
            return ToolResult.Error("缺少 source_path 或 destination_path。", "參數不正確");

        var source = context.Scope.Resolve(args.SourcePath);
        if (!source.IsAllowed)
            return ToolResult.Error(source.Error!, "無法複製");
        var destination = context.Scope.Resolve(args.DestinationPath);
        if (!destination.IsAllowed)
            return ToolResult.Error(destination.Error!, "無法複製");

        var sourceFull = source.FullPath!;
        var destinationFull = destination.FullPath!;
        var sourceDisplay = context.Scope.ToDisplayPath(sourceFull);
        var destinationDisplay = context.Scope.ToDisplayPath(destinationFull);

        if (FileWriteSupport.SameFullPath(sourceFull, destinationFull))
            return ToolResult.Error("來源與目的相同，拒絕複製。", "來源與目的相同");
        if (Directory.Exists(sourceFull))
            return ToolResult.Error($"「{sourceDisplay}」是資料夾，copy_file 只能複製檔案。", "是資料夾");
        if (!File.Exists(sourceFull))
            return ToolResult.Error($"來源檔案不存在：{sourceDisplay}", "找不到檔案");
        if (TextFileWriter.IsBlocked(destinationFull, out var extension))
            return ToolResult.Error(TextFileWriter.BlockedMessage(extension), "副檔名不允許");
        if (File.Exists(destinationFull) || Directory.Exists(destinationFull))
            return ToolResult.Error($"目的已存在：{destinationDisplay}（不會覆蓋）。", "目的已存在");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFull)!);
            await FileWriteSupport.CopyNewAsync(sourceFull, destinationFull, ct).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(destinationFull))
        {
            return ToolResult.Error($"目的已存在：{destinationDisplay}（不會覆蓋）。", "目的已存在");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"複製檔案失敗：{sourceDisplay} → {destinationDisplay}", "複製失敗");
        }

        var size = new FileInfo(destinationFull).Length;
        return ToolResult.Ok($"已複製 {sourceDisplay} → {destinationDisplay}（{FileWriteSupport.Size(size)}）。", $"複製 {FileWriteSupport.Size(size)}");
    }
}
