using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserUploadArgs(
    [property: Description("檔案欄位的元素，最近一次快照裡 [ref=…] 的內容，例如 e12")] string Ref,
    [property: Description("要上傳的檔案：相對於使用者加入的資料夾，或範圍內的絕對路徑")] string Path);

/// <summary>在網頁的檔案欄位選擇範圍內的檔案上傳（M18 D14，同 M10、M17 的範圍規則）。每次都請使用者確認。</summary>
public sealed class BrowserUploadTool(IBrowserSessions sessions) : ToolBase<BrowserUploadArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_upload", "上傳檔案",
        "在網頁的檔案欄位選擇要上傳的檔案（檔案必須在使用者加入的檔案或資料夾範圍內）。上傳會把檔案送到該網站，會先請使用者確認。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(120),
        RequiresScope: true, Group: ToolGroups.Browser, Summary: "把你加入的檔案上傳到網頁。", CanAlwaysAllow: false);

    protected override ToolPreview Describe(BrowserUploadArgs args, ToolContext context)
    {
        var host = sessions.CurrentUrl(context.ConversationId)?.Host ?? "網頁";
        var detail = FileWriteSupport.SafeDetail(() => $"上傳 {FileWriteSupport.Display(context.Scope, args.Path ?? "")} 到 {host}");
        return BrowserToolSupport.Preview(sessions, context.ConversationId, args.Ref, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(BrowserUploadArgs args, ToolContext context, CancellationToken ct)
    {
        if (!BrowserToolSupport.TryParseRef(args.Ref, out var @ref, out var error))
            return ToolResult.Error(error, "參數不正確");
        if (string.IsNullOrWhiteSpace(args.Path))
            return ToolResult.Error("缺少 path（要上傳的檔案）。", "參數不正確");

        var resolved = context.Scope.Resolve(args.Path);
        if (!resolved.IsAllowed)
            return ToolResult.Error(resolved.Error!, "無法讀取");

        var full = resolved.FullPath!;
        if (!File.Exists(full))
            return ToolResult.Error($"「{context.Scope.ToDisplayPath(full)}」不是檔案。", "不是檔案");

        try
        {
            var outcome = await sessions.UploadAsync(context.ConversationId, @ref, full, ct);
            return BrowserToolSupport.FromOutcome(outcome, "已上傳檔案");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
