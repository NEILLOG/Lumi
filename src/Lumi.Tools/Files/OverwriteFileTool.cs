using System.ComponentModel;
using System.Text;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record OverwriteFileArgs(
    [property: Description("要整份取代的既有檔案路徑：相對於使用者加入的資料夾，或範圍內的絕對路徑")] string Path,
    [property: Description("新的完整文字內容，會取代檔案原有的全部內容；單次最多 50 萬字")] string Text);

/// <summary>整份覆寫一個既有的文字檔。原內容會全部丟掉，所以權限是 Destructive（每次都要確認）。</summary>
public sealed class OverwriteFileTool(FileToolOptions options) : ToolBase<OverwriteFileArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "overwrite_file", "覆寫檔案",
        "用新內容整份取代一個既有的純文字檔，原有內容會全部丟掉、無法復原。只有使用者明確要求整份取代檔案時才呼叫；只想在末尾加內容請用 write_file（mode=append）。" +
        "檔案必須已存在；沿用原檔的編碼與換行，先寫暫存檔再取代，寫入失敗時原檔不變。每次都會請使用者確認。",
        ToolPermission.Destructive, ToolModes.Workbench, TimeSpan.FromSeconds(15), RequiresScope: true, Group: ToolGroups.FilesWrite, Summary: "用新內容覆蓋整個既有檔案。");

    protected override ToolPreview Describe(OverwriteFileArgs args, ToolContext context) =>
        FileWriteSupport.SafePreview(() => new ToolPreview(
            string.IsNullOrWhiteSpace(args.Path) ? null : FileWriteSupport.Display(context.Scope, args.Path),
            FileWriteSupport.SafeDetail(() => Impact(args, context))), args.Path);

    protected override Task<ToolResult> InvokeAsync(OverwriteFileArgs args, ToolContext context, CancellationToken ct) =>
        Task.Run(() => Overwrite(args, context), ct);

    /// <summary>確認卡片的影響說明：「覆寫：M 行（4.1 KB）→ N 行（0.8 KB）」；超過 1 MB 的舊檔只顯示大小。讀不到就沒有說明。</summary>
    private string? Impact(OverwriteFileArgs args, ToolContext context)
    {
        if (args.Text is null || string.IsNullOrWhiteSpace(args.Path))
            return null;
        var resolved = context.Scope.Resolve(args.Path);
        if (!resolved.IsAllowed || !File.Exists(resolved.FullPath))
            return null;

        var newLines = TextFileWriter.CountLines(args.Text);
        var oldSize = new FileInfo(resolved.FullPath!).Length;
        if (oldSize > FileWriteSupport.DetailSizeLimit)
            return $"覆寫：{FileWriteSupport.Size(oldSize)} → {newLines} 行（{FileWriteSupport.Size(NewSize(args.Text, TextEncodingKind.Utf8))}）";

        var info = TextFileWriter.Inspect(resolved.FullPath!);
        return $"覆寫：{info.Lines} 行（{FileWriteSupport.Size(oldSize)}）→ {newLines} 行（{FileWriteSupport.Size(NewSize(args.Text, info.Kind))}）";
    }

    private static long NewSize(string text, TextEncodingKind kind) =>
        TextFileWriter.EncodedSize(TextFileWriter.Normalize(text, TextFileWriter.Lf), kind);

    private ToolResult Overwrite(OverwriteFileArgs args, ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return ToolResult.Error("缺少 path（要覆寫的檔案路徑）。", "參數不正確");
        if (args.Text is null)
            return ToolResult.Error("缺少 text（新的文字內容）。", "參數不正確");
        if (args.Text.Length > options.MaxWriteChars)
            return ToolResult.Error($"內容太長（超過 {options.MaxWriteChars:N0} 字）。", "內容太長");

        var resolved = context.Scope.Resolve(args.Path);
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法覆寫");

        var full = resolved.FullPath!;
        var display = context.Scope.ToDisplayPath(full);
        if (TextFileWriter.IsBlocked(full, out var extension))
            return ToolResult.Error(TextFileWriter.BlockedMessage(extension), "副檔名不允許");
        if (Directory.Exists(full))
            return ToolResult.Error($"「{display}」是資料夾，不能覆寫。", "是資料夾");
        if (!File.Exists(full))
            return ToolResult.Error($"找不到檔案：{display}。要建立新檔請用 write_file（mode=create）。", "找不到檔案");

        TextFileInfo info;
        try
        {
            info = TextFileWriter.Inspect(full);
        }
        catch (DecoderFallbackException)
        {
            return ToolResult.Error($"{display}：{TextFiles.InvalidUtf8Message}", "無法覆寫");
        }
        catch (InvalidDataException)
        {
            return ToolResult.Error($"{display}：{TextFiles.BinaryMessage}", "無法覆寫");
        }

        try
        {
            TextFileWriter.Overwrite(full, args.Text, info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"無法覆寫：{display}（原檔案內容未變）", "無法覆寫");
        }

        var newLines = TextFileWriter.CountLines(args.Text);
        return ToolResult.Ok($"已覆寫 {display}：{info.Lines:N0} 行 → {newLines:N0} 行。", $"覆寫為 {newLines:N0} 行");
    }
}
