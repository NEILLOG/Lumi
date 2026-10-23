using System.ComponentModel;
using System.Text;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record WriteFileArgs(
    [property: Description("要寫入的檔案路徑：相對於使用者加入的資料夾（例如 notes/todo.md），或範圍內的絕對路徑；上層資料夾不存在時會自動建立")] string Path,
    [property: Description("create：建立新檔，檔案已存在就失敗；append：把文字加在檔案末尾，檔案不存在就建立。兩者都不會覆蓋既有內容")] string Mode,
    [property: Description("要寫入的純文字內容；單次最多 50 萬字，較長的內容請分次用 append 寫入")] string Text,
    [property: Description("新檔的編碼：utf-8（預設）、utf-8-bom 或 utf-16；只對新檔有效，既有檔案沿用原本的編碼")] string? Encoding = null);

/// <summary>寫入文字檔：建立新檔或在末尾追加。絕不覆蓋既有內容（要整份取代請用 overwrite_file）。</summary>
public sealed class WriteFileTool(FileToolOptions options) : ToolBase<WriteFileArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "write_file", "寫入檔案",
        "在使用者加入的資料夾或檔案範圍內寫入一個純文字檔。mode=create 建立新檔（檔案已存在就失敗）；mode=append 把文字加在既有檔案的末尾（檔案不存在就建立）。" +
        "兩種模式都不會覆蓋既有內容；要整份取代檔案請用 overwrite_file。只有使用者明確要求建立或寫入檔案時才呼叫。" +
        "不能建立或修改 .docx、.xlsx、.pptx、.pdf、壓縮檔等複合文件與封存檔。追加時沿用原檔的編碼與換行。寫入前會請使用者確認。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(15), RequiresScope: true);

    protected override ToolPreview Describe(WriteFileArgs args, ToolContext context) =>
        FileWriteSupport.SafePreview(() => new ToolPreview(
            string.IsNullOrWhiteSpace(args.Path) ? null : FileWriteSupport.Display(context.Scope, args.Path),
            FileWriteSupport.SafeDetail(() => Impact(args, context))), args.Path);

    protected override Task<ToolResult> InvokeAsync(WriteFileArgs args, ToolContext context, CancellationToken ct) =>
        Task.Run(() => Write(args, context), ct);

    /// <summary>確認卡片的影響說明：新增幾行，或追加幾行（檔案現有幾行）。</summary>
    private string? Impact(WriteFileArgs args, ToolContext context)
    {
        if (args.Text is null || string.IsNullOrWhiteSpace(args.Path))
            return null;
        var resolved = context.Scope.Resolve(args.Path);
        if (!resolved.IsAllowed)
            return null;   // 範圍外的路徑一定會失敗：不顯示說明
        var lines = TextFileWriter.CountLines(args.Text);
        var full = resolved.FullPath!;
        if (args.Mode != "append" || !File.Exists(full))
            return $"新增 · {lines} 行";

        var size = new FileInfo(full).Length;
        if (size > FileWriteSupport.DetailSizeLimit)
            return $"追加 {lines} 行（檔案大小 {FileWriteSupport.Size(size)}）";
        var info = TextFileWriter.Inspect(full);
        return $"追加 {lines} 行（檔案現有 {info.Lines} 行）";
    }

    private ToolResult Write(WriteFileArgs args, ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return ToolResult.Error("缺少 path（要寫入的檔案路徑）。", "參數不正確");
        if (args.Mode is null)
            return ToolResult.Error("缺少 mode（create 或 append）。", "參數不正確");
        if (args.Mode is not ("create" or "append"))
            return ToolResult.Error($"mode 只能是 create 或 append，不接受「{args.Mode}」。", "參數不正確");
        if (args.Text is null)
            return ToolResult.Error("缺少 text（要寫入的文字內容）。", "參數不正確");
        if (args.Text.Length == 0 && args.Mode == "append")
            return ToolResult.Error("追加的內容不能是空的。", "參數不正確");
        if (args.Text.Length > options.MaxWriteChars)
            return ToolResult.Error($"內容太長（超過 {options.MaxWriteChars:N0} 字），請分成幾次用 append 寫入。", "內容太長");
        if (!TextFileWriter.TryParseEncoding(args.Encoding, out var kind))
            return ToolResult.Error("encoding 只能是 utf-8、utf-8-bom 或 utf-16。", "參數不正確");

        var resolved = context.Scope.Resolve(args.Path);
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法寫入");

        var full = resolved.FullPath!;
        var display = context.Scope.ToDisplayPath(full);
        if (TextFileWriter.IsBlocked(full, out var extension))
            return ToolResult.Error(TextFileWriter.BlockedMessage(extension), "副檔名不允許");
        if (Directory.Exists(full))
            return ToolResult.Error($"「{display}」是資料夾，不能寫入。", "是資料夾");

        try
        {
            if (File.Exists(full))
            {
                if (args.Mode == "create")
                    return ToolResult.Error($"「{display}」已經存在，不會覆蓋。要加在末尾請用 mode=append；要整份取代請用 overwrite_file。", "檔案已存在");
                return Append(args.Text, full, display);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var lines = TextFileWriter.CountLines(args.Text);
            try
            {
                TextFileWriter.CreateNew(full, args.Text, kind);
            }
            catch (IOException) when (File.Exists(full))
            {
                // 檢查之後才出現的同名檔案（競爭）：一樣不覆蓋
                return ToolResult.Error($"「{display}」已經存在，不會覆蓋。", "檔案已存在");
            }
            return ToolResult.Ok($"已建立 {display}（{lines:N0} 行）。", $"新增 {lines:N0} 行");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"無法寫入：{display}", "無法寫入");
        }
    }

    private static ToolResult Append(string text, string full, string display)
    {
        TextFileInfo info;
        try
        {
            info = TextFileWriter.Inspect(full);
        }
        catch (DecoderFallbackException)
        {
            return ToolResult.Error($"{display}：{TextFiles.InvalidUtf8Message}", "無法寫入");
        }
        catch (InvalidDataException)
        {
            return ToolResult.Error($"{display}：{TextFiles.BinaryMessage}", "無法寫入");
        }

        var lines = TextFileWriter.CountLines(text);
        TextFileWriter.Append(full, text, info);
        var total = info.Lines + lines;
        return ToolResult.Ok($"已追加 {lines:N0} 行到 {display}（檔案現在共 {total:N0} 行）。", $"追加 {lines:N0} 行");
    }
}
