using System.ComponentModel;
using System.Globalization;
using Lumi.Core.Tools;

namespace Lumi.Tools;

public sealed record GetCurrentTimeArgs(
    [property: Description("IANA 時區名稱，例如 Asia/Taipei 或 America/New_York；不給就用使用者電腦的時區")]
    string? Timezone = null);

public sealed class GetCurrentTimeTool : ToolBase<GetCurrentTimeArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "get_current_time", "查詢目前時間",
        "取得目前的日期與時間。你不知道現在幾點、今天幾號或星期幾的時候一定要呼叫它，不要憑印象回答。",
        ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5), Group: ToolGroups.General, Summary: "查詢現在的日期與時間。");

    protected override ToolPreview Describe(GetCurrentTimeArgs args, ToolContext context) =>
        new(string.IsNullOrWhiteSpace(args.Timezone) ? null : args.Timezone.Trim(), null);

    protected override Task<ToolResult> InvokeAsync(GetCurrentTimeArgs args, ToolContext context, CancellationToken ct)
    {
        TimeZoneInfo zone;
        if (string.IsNullOrWhiteSpace(args.Timezone))
        {
            zone = context.Time.LocalTimeZone;
        }
        else
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(args.Timezone.Trim());
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return Task.FromResult(ToolResult.Error(
                    $"不認得的時區：{args.Timezone.Trim()}。請使用 IANA 名稱，例如 Asia/Taipei。", "不認得的時區"));
            }
        }

        var now = TimeZoneInfo.ConvertTime(context.Time.GetUtcNow(), zone);
        var offset = now.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss}（{zone.Id}，UTC{sign}{offset.Duration():hh\\:mm}），{now.DayOfWeek}");
        var summary = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm}（{zone.Id}）");
        return Task.FromResult(ToolResult.Ok(text, summary));
    }
}
