using System.ComponentModel;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record ShowDetailsArgs(
    [property: Description("這份內容的標題，十個字左右，例如「C# 讀檔範例」")] string Title,
    [property: Description("完整內容，用 Markdown 寫（標題、清單、表格、程式碼區塊都可以）")] string Markdown);

/// <summary>
/// 小精靈專用：氣泡只有兩句話，放不下的長內容（程式碼、步驟、清單、表格、長篇解釋）改放到工作台。
/// 工具本身什麼都不存：內容就在助理訊息的工具呼叫輸入裡，工作台直接從那裡顯示，重開程式也還在（M5）。
/// 只能在小精靈模式執行：模式不符時由對話迴圈直接回錯誤（定義一律送出是為了守住 prompt 快取）。
/// </summary>
public sealed class ShowDetailsTool : ToolBase<ShowDetailsArgs>
{
    /// <summary>工具名稱。工作台靠它認出這種卡片，要畫成展開的內容卡而不是收合的工具卡。</summary>
    public const string Name = "show_details";

    public const int MaxTitleLength = 100;
    public const int MaxMarkdownLength = 20_000;

    public override ToolDescriptor Descriptor { get; } = new(
        Name, "整理完整內容",
        "把放不進一兩句話的完整內容（程式碼、步驟、清單、表格、長篇的說明或翻譯）放到工作台，讓使用者之後點開查看。"
        + "只在小精靈模式使用；呼叫之後，回答只用一句話說重點，不要重複內容。",
        ToolPermission.ReadOnly, ToolModes.Sprite, TimeSpan.FromSeconds(5));

    protected override ToolPreview Describe(ShowDetailsArgs args) => new(args.Title?.Trim(), null);

    protected override Task<ToolResult> InvokeAsync(ShowDetailsArgs args, ToolContext context, CancellationToken ct)
    {
        var title = args.Title?.Trim() ?? "";
        var markdown = args.Markdown?.Trim() ?? "";

        if (title.Length == 0 || markdown.Length == 0)
            return Task.FromResult(ToolResult.Error("標題與內容都不能是空的。", "缺少標題或內容"));
        if (title.Length > MaxTitleLength)
            return Task.FromResult(ToolResult.Error($"標題太長（上限 {MaxTitleLength} 字），請縮短。", "標題太長"));
        if (markdown.Length > MaxMarkdownLength)
            return Task.FromResult(ToolResult.Error($"內容太長（{markdown.Length} 字，上限 {MaxMarkdownLength} 字），請精簡後再呼叫一次。", "內容太長"));

        return Task.FromResult(ToolResult.Ok("已放到工作台，使用者可以點開查看。請用一句話告訴使用者重點。", $"{markdown.Length} 字"));
    }
}
