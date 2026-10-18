using System.ComponentModel;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record DeleteNoteArgs(
    [property: Description("要刪除的筆記檔名（含副檔名），簡單名稱即可，不要包含資料夾路徑")] string Name);

/// <summary>示範工具（M3）：只為了驗收「Destructive 每次都要詢問」的流程，M4 有真正的檔案工具後移除。</summary>
public sealed class DeleteNoteTool(ToolsOptions options) : ToolBase<DeleteNoteArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "delete_note", "刪除筆記",
        "刪除筆記資料夾裡的一個筆記檔。刪除無法復原，只有使用者明確要求刪除時才呼叫。",
        ToolPermission.Destructive, ToolModes.Workbench, TimeSpan.FromSeconds(10));

    protected override ToolPreview Describe(DeleteNoteArgs args)
    {
        var name = NoteFiles.Normalize(args.Name);
        return new(name ?? args.Name?.Trim(), name is null ? null : $"將永久刪除筆記資料夾裡的 {name}");
    }

    protected override Task<ToolResult> InvokeAsync(DeleteNoteArgs args, ToolContext context, CancellationToken ct)
    {
        var name = NoteFiles.Normalize(args.Name);
        if (name is null)
            return Task.FromResult(ToolResult.Error(NoteFiles.InvalidNameMessage, "檔名不合法"));

        var path = NoteFiles.ResolvePath(options.ResolveNotesDirectory(), name);
        if (path is null)
            return Task.FromResult(ToolResult.Error(NoteFiles.InvalidNameMessage, "檔名不合法"));
        if (!File.Exists(path))
            return Task.FromResult(ToolResult.Error($"找不到筆記「{name}」。", "找不到檔案"));

        File.Delete(path);
        return Task.FromResult(ToolResult.Ok($"已刪除筆記「{name}」。", $"已刪除 {name}"));
    }
}
