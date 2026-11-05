using System.ComponentModel;
using Lumi.Core.Tools;
using Lumi.Core.Updates;

namespace Lumi.Tools;

public sealed record AppHelpArgs(
    [property: Description("主題代號，從目錄取得；不給就回傳目錄")]
    string? Topic = null);

/// <summary>查 Lumi 自己的使用說明（<c>docs/guide</c> 內嵌在組件裡）。</summary>
public sealed class AppHelpTool : ToolBase<AppHelpArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "app_help", "查使用說明",
        "查 Lumi 自己的使用說明。使用者問到 Lumi 的功能、操作、設定或錯誤訊息時先呼叫。不帶參數取得目錄，再用 topic 讀取整篇。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5), Group: ToolGroups.General, Summary: "查 Lumi 自己的使用說明。");

    protected override ToolPreview Describe(AppHelpArgs args, ToolContext context) =>
        new(string.IsNullOrWhiteSpace(args.Topic) ? null : args.Topic.Trim(), null);

    protected override Task<ToolResult> InvokeAsync(AppHelpArgs args, ToolContext context, CancellationToken ct)
    {
        var topics = UserGuide.Topics;

        if (string.IsNullOrWhiteSpace(args.Topic))
        {
            var lines = new List<string> { $"Lumi {AppVersion.Current} 使用說明" };
            lines.AddRange(topics.Select(t => $"{t.Id}：{t.Title}：{t.Summary}"));
            return Task.FromResult(ToolResult.Ok(string.Join('\n', lines), $"共 {topics.Count} 篇"));
        }

        var topic = args.Topic.Trim();
        var found = UserGuide.Find(topic);
        if (found is null)
        {
            var available = string.Join("、", topics.Select(t => t.Id));
            return Task.FromResult(ToolResult.Error(
                $"找不到「{topic}」這個主題。可用的主題：{available}", "找不到這個主題"));
        }

        var text = UserGuide.StripImages(found.Body).TrimEnd()
            + $"\n\n線上版：{UserGuideLinks.For(found.Id)}";
        return Task.FromResult(ToolResult.Ok(text, found.Title));
    }
}
