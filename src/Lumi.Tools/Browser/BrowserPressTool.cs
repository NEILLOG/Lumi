using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserPressArgs(
    [property: Description("要按的鍵：Enter、Tab、Escape、Backspace、Delete、方向鍵（ArrowUp、ArrowDown、ArrowLeft、ArrowRight）、Home、End、PageUp、PageDown、Space")] string Key);

/// <summary>在目前網頁按一個鍵（M18）。只接受 <see cref="BrowserRules.AllowedKeys"/> 的按鍵。</summary>
public sealed class BrowserPressTool(IBrowserSessions sessions) : ToolBase<BrowserPressArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "browser_press", "按鍵",
        "在目前網頁按一個鍵（例如 Enter 送出表單、Escape 關閉對話框）。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(30),
        Group: ToolGroups.Browser, Summary: "在網頁上按一個鍵。");

    protected override ToolPreview Describe(BrowserPressArgs args, ToolContext context)
    {
        if (!BrowserRules.TryParseKey(args.Key, out var key, out _))
            return ToolPreview.None;
        var host = sessions.CurrentUrl(context.ConversationId)?.Host;
        return new ToolPreview(host is null ? $"按下 {key}" : $"按下 {key} · {host}", null);
    }

    protected override async Task<ToolResult> InvokeAsync(BrowserPressArgs args, ToolContext context, CancellationToken ct)
    {
        if (!BrowserRules.TryParseKey(args.Key, out var key, out var error))
            return ToolResult.Error(error, "參數不正確");
        try
        {
            var outcome = await sessions.PressAsync(context.ConversationId, key, ct);
            return BrowserToolSupport.FromOutcome(outcome, $"已按下 {key}");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }
}
