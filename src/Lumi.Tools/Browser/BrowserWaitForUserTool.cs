using System.ComponentModel;
using Lumi.Core.Browser;
using Lumi.Core.Tools;

namespace Lumi.Tools.Browser;

public sealed record BrowserWaitForUserArgs(
    [property: Description("要告訴使用者的話：請他在瀏覽器視窗做什麼，例如「請在視窗登入公司帳號」，最多 200 字")] string Message,
    [property: Description("完成條件之一：網址包含這段文字（例如登入後的網址）時繼續")] string? DoneWhenUrlContains = null,
    [property: Description("完成條件之一：頁面出現這段文字（例如使用者的名字或「登出」）時繼續")] string? DoneWhenText = null,
    [property: Description("最多等幾秒，預設 180，範圍 10 到 600")] int? TimeoutSeconds = null);

/// <summary>打開瀏覽器視窗，請使用者自己完成操作（登入、輸入密碼、過驗證碼），等到完成條件成立（M18 D9）。每次都請使用者確認。</summary>
public sealed class BrowserWaitForUserTool(IBrowserSessions sessions) : ToolBase<BrowserWaitForUserArgs>
{
    private const int MaxMessageLength = 200;

    public override ToolDescriptor Descriptor { get; } = new(
        "browser_wait_for_user", "等待使用者操作",
        "打開瀏覽器視窗，請使用者自己完成一件事（登入、輸入密碼、過驗證碼…），等到頁面符合你指定的「完成」條件才繼續。" +
        "必須用 done_when_url_contains（網址包含某段文字，例如登入後的網址）或 done_when_text（頁面出現某段文字，例如使用者的名字或「登出」）指定條件。" +
        "不要自己輸入密碼；遇到密碼欄位一律用這個工具。每次都會請使用者確認。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(610),
        Group: ToolGroups.Browser, Summary: "請你在瀏覽器視窗自己完成操作（登入、驗證碼）。", CanAlwaysAllow: false);

    protected override ToolPreview Describe(BrowserWaitForUserArgs args, ToolContext context)
    {
        var host = sessions.CurrentUrl(context.ConversationId)?.Host;
        var target = host is null ? "等待你在瀏覽器操作" : $"等待你在瀏覽器操作 · {host}";

        var message = Flatten(args.Message);
        var lines = new List<string>();
        if (message.Length > 0)
            lines.Add(message);
        if (BrowserRules.ValidateWait(args.DoneWhenUrlContains, args.DoneWhenText, args.TimeoutSeconds, out var condition, out var timeout) is null)
            lines.Add($"Lumi 會打開瀏覽器視窗，等到{DescribeCondition(condition)}才繼續，最多 {(int)timeout.TotalSeconds} 秒。");

        return new ToolPreview(target, lines.Count == 0 ? null : string.Join("\n", lines));
    }

    protected override async Task<ToolResult> InvokeAsync(BrowserWaitForUserArgs args, ToolContext context, CancellationToken ct)
    {
        var message = (args.Message ?? "").Trim();
        if (message.Length == 0)
            return ToolResult.Error("缺少 message（要告訴使用者的話）。", "參數不正確");
        if (message.Length > MaxMessageLength)
            return ToolResult.Error($"message 太長（超過 {MaxMessageLength} 字）。", "參數不正確");

        var error = BrowserRules.ValidateWait(args.DoneWhenUrlContains, args.DoneWhenText, args.TimeoutSeconds, out var condition, out var timeout);
        if (error is not null)
            return ToolResult.Error(error, "參數不正確");

        try
        {
            var result = await sessions.WaitForUserAsync(context.ConversationId, condition, timeout, ct);
            return result.Completed
                ? BrowserToolSupport.FromOutcome(new BrowserOutcome("使用者已完成操作。", result.Snapshot), "使用者已完成")
                : BrowserToolSupport.FromOutcome(new BrowserOutcome(result.Message, result.Snapshot), "等待逾時");
        }
        catch (BrowserException e)
        {
            return BrowserToolSupport.Failure(e);
        }
    }

    /// <summary>確認卡片用：去掉換行、最多 200 字。</summary>
    private static string Flatten(string? raw)
    {
        var text = (raw ?? "").ReplaceLineEndings(" ").Trim();
        return text.Length <= MaxMessageLength ? text : text[..(MaxMessageLength - 1)] + "…";
    }

    private static string DescribeCondition(BrowserWaitCondition condition) => (condition.UrlContains, condition.TextVisible) switch
    {
        (not null, not null) => $"網址包含「{condition.UrlContains}」或頁面出現「{condition.TextVisible}」",
        (not null, null) => $"網址包含「{condition.UrlContains}」",
        _ => $"頁面出現「{condition.TextVisible}」",
    };
}
