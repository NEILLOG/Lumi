using System.ComponentModel;
using System.Text.Json;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Tools.Reminders;

public sealed record StartTimerArgs(
    [property: Description("計時長度（秒），10～86400（24 小時）。例如 10 分鐘是 600")] int DurationSeconds,
    [property: Description("計時器名稱（選填，最多 100 字）；省略時為「計時器」")] string? Label = null);

/// <summary>啟動計時器（舊版 <c>timer_start</c>，M10 10E2）：時間到響一次。</summary>
public sealed class StartTimerTool(IReminderService reminders, ReminderOptions options) : ToolBase<StartTimerArgs>
{
    private const string DefaultLabel = "計時器";

    public override ToolDescriptor Descriptor { get; } = new(
        "start_timer", "啟動計時器",
        "啟動一個倒數計時器，時間到會在 Lumi 裡跳出通知，例如「幫我計時 10 分鐘」。時長以秒指定，10 秒到 24 小時。"
        + "只在使用者明確要求計時時使用。Lumi 沒有在執行時（例如程式沒開）不會響，不要承諾一定會準時提醒。",
        ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(10));

    protected override ToolPreview Describe(StartTimerArgs args, ToolContext context)
    {
        var label = NormalizeLabel(args.Label);
        string? detail = null;
        if (IsDurationValid(args.DurationSeconds))
        {
            var expiresAt = context.Time.GetUtcNow().AddSeconds(args.DurationSeconds);
            detail = $"{ReminderFormat.Duration(args.DurationSeconds)}後（約 {ReminderFormat.Clock(expiresAt, TimeZoneInfo.Local)}）";
        }
        return new ToolPreview(label, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(StartTimerArgs args, ToolContext context, CancellationToken ct)
    {
        if (!IsDurationValid(args.DurationSeconds))
            return ToolResult.Error(
                $"duration_seconds 必須介於 {options.MinTimerSeconds} 到 {options.MaxTimerSeconds} 秒 ({options.MaxTimerSeconds / 3600} 小時) 之間。", "時長不正確");

        var label = NormalizeLabel(args.Label);
        if (label.Length > options.MaxTitleChars)
            return ToolResult.Error($"label 太長（上限 {options.MaxTitleChars} 字），請縮短後再試。", "參數不正確");

        ScheduledItem item;
        try
        {
            item = await reminders.StartTimerAsync(label, args.DurationSeconds, ct);
        }
        catch (ReminderException e)
        {
            return ToolResult.Error(e.Message, "無法啟動");
        }

        var expiresAt = item.NextFireAt!.Value;
        var result = new TimerResult(
            item.Id,
            item.Title,
            args.DurationSeconds,
            ReminderFormat.Iso(expiresAt, TimeZoneInfo.Local)!,
            ReminderFormat.Local(expiresAt, TimeZoneInfo.Local));
        var summary = $"已開始計時 · {ReminderFormat.Duration(args.DurationSeconds)} · 約 {ReminderFormat.Clock(expiresAt, TimeZoneInfo.Local)}";
        return ToolResult.Ok(JsonSerializer.Serialize(result, ReminderFormat.JsonOptions), summary);
    }

    private bool IsDurationValid(int seconds) => seconds >= options.MinTimerSeconds && seconds <= options.MaxTimerSeconds;

    /// <summary>空白或沒給就是「計時器」；有值時去頭尾空白。</summary>
    private static string NormalizeLabel(string? raw) => string.IsNullOrWhiteSpace(raw) ? DefaultLabel : raw.Trim();

    private sealed record TimerResult(
        string Id,
        string Label,
        int DurationSeconds,
        string ExpiresAt,
        string ExpiresAtLocal);
}
