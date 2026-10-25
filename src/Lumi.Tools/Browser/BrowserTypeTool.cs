using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserTypeArgs(
    [property: Description("要輸入文字的欄位，最近一次快照裡 [ref=…] 的內容，例如 e12")] string Ref,
    [property: Description("要輸入的文字，最多 10000 字。不要輸入密碼——遇到密碼欄位請用 browser_wait_for_user")] string Text,
    [property: Description("true（預設）＝先清空欄位再輸入；false＝接在原有文字後面")] bool? Replace = null,
    [property: Description("true＝輸入後按 Enter 送出")] bool? Submit = null);

/// <summary>在網頁的文字欄位輸入文字（M18 D7：不往密碼欄位輸入）。確認卡片不顯示輸入的內容。</summary>
public sealed class BrowserTypeTool(IBrowserSessions sessions) : ToolBase<BrowserTypeArgs>
{
    private const int MaxTextLength = 10_000;

    public override ToolDescriptor Descriptor { get; } = new(
        "browser_type", "輸入文字",
        "在網頁的文字欄位輸入文字。不能輸入密碼。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(60),
        Group: ToolGroups.Browser, Summary: "在網頁的文字欄位輸入文字。");

    protected override ToolPreview Describe(BrowserTypeArgs args, ToolContext context)
    {
        // 不顯示輸入的內容（可能含個資）；只說字數與是否送出
        var detail = $"輸入 {args.Text.Length} 個字" + (args.Submit == true ? "，並按 Enter 送出" : "");
        return BrowserToolSupport.Preview(sessions, context.ConversationId, args.Ref, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(BrowserTypeArgs args, ToolContext context, CancellationToken ct)
    {
        if (!BrowserToolSupport.TryParseRef(args.Ref, out var @ref, out var error))
            return ToolResult.Error(error, "參數不正確");
        if (args.Text.Length > MaxTextLength)
            return ToolResult.Error($"text 太長（超過 {MaxTextLength} 字）。", "參數不正確");
        try
        {
            var outcome = await sessions.TypeAsync(context.ConversationId, @ref, args.Text, args.Replace ?? true, args.Submit ?? false, ct);
            return BrowserToolSupport.FromOutcome(outcome, "已輸入文字");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
