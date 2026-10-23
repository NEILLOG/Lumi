using System.ComponentModel;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record MoveFileArgs(
    [property: Description("來源檔案路徑：相對於使用者加入的資料夾，或範圍內的絕對路徑")] string SourcePath,
    [property: Description("目的檔案路徑：同資料夾內換名、或移到別的資料夾；目的已存在會失敗")] string DestinationPath);

/// <summary>移動或改名範圍內的一個檔案。目的已存在就失敗，不覆蓋。</summary>
public sealed class MoveFileTool : ToolBase<MoveFileArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "move_file", "移動檔案",
        "移動範圍內的一個檔案，或在同一資料夾裡改名（改名也用這個工具）。目的檔已存在時失敗，不會覆蓋。只能移動檔案，不能移動資料夾；" +
        "只改大小寫也可以。目的的上層資料夾不存在時會自動建立。不能移成 .docx、.xlsx、.pptx、.pdf、壓縮檔等副檔名。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromMinutes(5), RequiresScope: true);

    protected override ToolPreview Describe(MoveFileArgs args, ToolContext context) =>
        FileWriteSupport.SafePreview(() =>
        {
            var source = string.IsNullOrWhiteSpace(args.SourcePath) ? "" : FileWriteSupport.Display(context.Scope, args.SourcePath);
            var destination = string.IsNullOrWhiteSpace(args.DestinationPath) ? "" : FileWriteSupport.Display(context.Scope, args.DestinationPath);
            return new ToolPreview(source.Length == 0 ? null : source, $"{source} → {destination}");
        }, args.SourcePath);

    protected override Task<ToolResult> InvokeAsync(MoveFileArgs args, ToolContext context, CancellationToken ct) =>
        MoveAsync(args, context, ct);

    private static async Task<ToolResult> MoveAsync(MoveFileArgs args, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.SourcePath) || string.IsNullOrWhiteSpace(args.DestinationPath))
            return ToolResult.Error("缺少 source_path 或 destination_path。", "參數不正確");

        var source = context.Scope.Resolve(args.SourcePath);
        if (!source.IsAllowed)
            return ToolResult.Error(source.Error!, "無法移動");
        var destination = context.Scope.Resolve(args.DestinationPath);
        if (!destination.IsAllowed)
            return ToolResult.Error(destination.Error!, "無法移動");

        var sourceFull = source.FullPath!;
        var destinationFull = destination.FullPath!;
        var sourceDisplay = context.Scope.ToDisplayPath(sourceFull);
        var destinationDisplay = context.Scope.ToDisplayPath(destinationFull);

        if (string.Equals(sourceFull, destinationFull, StringComparison.Ordinal))
            return ToolResult.Error("來源與目的相同，拒絕移動。", "來源與目的相同");
        if (Directory.Exists(sourceFull))
            return ToolResult.Error($"「{sourceDisplay}」是資料夾，move_file 只能移動檔案。", "是資料夾");
        if (!File.Exists(sourceFull))
            return ToolResult.Error($"來源檔案不存在：{sourceDisplay}", "找不到檔案");
        if (TextFileWriter.IsBlocked(destinationFull, out var extension))
            return ToolResult.Error(TextFileWriter.BlockedMessage(extension), "副檔名不允許");

        // 不分大小寫的檔案系統上，只改大小寫時目的「存在」其實就是來源本身：用暫存名兩段式改名，不走存在檢查
        var caseOnly = CaseOnlyRename(sourceFull, destinationFull);
        if (!caseOnly && (File.Exists(destinationFull) || Directory.Exists(destinationFull)))
            return ToolResult.Error($"目的已存在：{destinationDisplay}（不會覆蓋）。", "目的已存在");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFull)!);
            if (caseOnly)
                RenameThroughTemporaryName(sourceFull, destinationFull);
            else
                await MoveOneStepAsync(sourceFull, destinationFull, ct).ConfigureAwait(false);
        }
        catch (IOException) when (!caseOnly && File.Exists(destinationFull))
        {
            return ToolResult.Error($"目的已存在：{destinationDisplay}（不會覆蓋）。", "目的已存在");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"移動檔案失敗：{sourceDisplay} → {destinationDisplay}（來源檔案保留於原處）", "移動失敗");
        }

        return ToolResult.Ok($"已移動 {sourceDisplay} → {destinationDisplay}。", $"移動到 {destinationDisplay}");
    }

    /// <summary>來源與目的只差大小寫，而且作業系統不分大小寫：這是同一個檔案的改名。</summary>
    internal static bool CaseOnlyRename(string source, string destination) =>
        FileScopeRules.ForCurrentOs.CaseInsensitive
        && !string.Equals(source, destination, StringComparison.Ordinal)
        && string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);

    private static async Task MoveOneStepAsync(string source, string destination, CancellationToken ct)
    {
        try
        {
            File.Move(source, destination, overwrite: false);   // 同磁碟區：一次改名；目的存在就丟 IOException
        }
        catch (IOException) when (!File.Exists(destination) && File.Exists(source))
        {
            // 跨磁碟區時系統不能直接改名：複製、比對大小、再刪來源
            await FileWriteSupport.MoveAcrossVolumesAsync(source, destination, ct).ConfigureAwait(false);
        }
    }

    /// <summary>只改大小寫：先改成暫存名再改成目標名；第二步失敗時改回來源名。</summary>
    private static void RenameThroughTemporaryName(string source, string destination)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(source)!, $"{Path.GetFileName(source)}.rename_tmp_{Guid.NewGuid():N}");
        File.Move(source, temporary, overwrite: false);
        try
        {
            File.Move(temporary, destination, overwrite: false);
        }
        catch
        {
            File.Move(temporary, source, overwrite: false);
            throw;
        }
    }
}
