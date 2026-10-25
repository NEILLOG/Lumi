using System.ComponentModel;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record CreateFolderArgs(
    [property: Description("要建立的資料夾路徑：相對於使用者加入的資料夾，或範圍內的絕對路徑；上層資料夾不存在時一併建立")] string Path);

/// <summary>在範圍內建立資料夾。已存在不是錯誤；同名的檔案存在才失敗。</summary>
public sealed class CreateFolderTool : ToolBase<CreateFolderArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "create_folder", "建立資料夾",
        "在使用者加入的範圍內建立一個新資料夾（上層資料夾不存在時一併建立）。資料夾已經存在時會回報已存在，不視為錯誤；同名的檔案存在時失敗。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(10), RequiresScope: true, Group: ToolGroups.FilesWrite, Summary: "建立資料夾。");

    protected override ToolPreview Describe(CreateFolderArgs args, ToolContext context) =>
        FileWriteSupport.SafePreview(() => new ToolPreview(
            string.IsNullOrWhiteSpace(args.Path) ? null : FileWriteSupport.Display(context.Scope, args.Path),
            "建立資料夾"), args.Path);

    protected override Task<ToolResult> InvokeAsync(CreateFolderArgs args, ToolContext context, CancellationToken ct) =>
        Task.Run(() => Create(args, context), ct);

    private static ToolResult Create(CreateFolderArgs args, ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(args.Path))
            return ToolResult.Error("缺少 path（要建立的資料夾路徑）。", "參數不正確");

        var resolved = context.Scope.Resolve(args.Path);
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法建立");

        var full = resolved.FullPath!;
        var display = context.Scope.ToDisplayPath(full);
        if (File.Exists(full))
            return ToolResult.Error($"已存在同名的檔案：{display}，無法建立同名資料夾。", "同名檔案已存在");
        if (Directory.Exists(full))
            return ToolResult.Ok($"資料夾已存在：{display}。", "已存在");

        try
        {
            Directory.CreateDirectory(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error($"無法建立資料夾：{display}", "無法建立");
        }
        return ToolResult.Ok($"已建立資料夾 {display}。", "已建立");
    }
}
