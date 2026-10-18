using System.ComponentModel;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record SaveNoteArgs(
    [property: Description("筆記檔名，簡單名稱即可，例如 購物清單 或 idea.md；不要包含資料夾路徑")] string Name,
    [property: Description("筆記的文字內容")] string Content);

/// <summary>示範工具（M3）：只為了驗收「Write 要先詢問」的流程，M4 有真正的檔案工具後移除。</summary>
public sealed class SaveNoteTool(ToolsOptions options) : ToolBase<SaveNoteArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "save_note", "儲存筆記",
        "把一段文字存成筆記檔（放在使用者的筆記資料夾）。使用者明確要你記下、存起來時才呼叫；同名的筆記已存在時不會覆蓋。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(10));

    protected override ToolPreview Describe(SaveNoteArgs args)
    {
        var name = NoteFiles.Normalize(args.Name);
        return new(name ?? args.Name?.Trim(), name is null ? null : $"將寫入 {args.Content?.Length ?? 0} 字到筆記資料夾的 {name}");
    }

    protected override async Task<ToolResult> InvokeAsync(SaveNoteArgs args, ToolContext context, CancellationToken ct)
    {
        var name = NoteFiles.Normalize(args.Name);
        if (name is null)
            return ToolResult.Error(NoteFiles.InvalidNameMessage, "檔名不合法");
        if (args.Content is null || args.Content.Length > NoteFiles.MaxContentChars)
            return ToolResult.Error($"內容不能是空的，也不能超過 {NoteFiles.MaxContentChars} 字。", "內容不合法");

        var directory = options.ResolveNotesDirectory();
        var path = NoteFiles.ResolvePath(directory, name);
        if (path is null)
            return ToolResult.Error(NoteFiles.InvalidNameMessage, "檔名不合法");

        Directory.CreateDirectory(directory);
        try
        {
            // CreateNew：同名已存在就失敗，Write 權限不包含覆蓋別人的內容
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(args.Content.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (IOException) when (File.Exists(path))
        {
            return ToolResult.Error($"筆記「{name}」已經存在，不會覆蓋。請換一個檔名。", "檔案已存在");
        }

        return ToolResult.Ok($"已儲存筆記「{name}」（{args.Content.Length} 字）。", $"已儲存 {name}");
    }
}
