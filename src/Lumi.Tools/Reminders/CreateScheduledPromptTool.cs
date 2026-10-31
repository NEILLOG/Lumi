using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Tools.Reminders;

public sealed record CreateScheduledPromptArgs(
    [property: Description("排程名稱（必填，1～100 字），排程清單與歷史紀錄會顯示這個名稱")] string Name,
    [property: Description("到點時要做的事（必填，1～4000 字）。寫成一次就能獨立完成的完整指令：排程執行時沒有人可以回答問題，也看不到這則對話；不要寫「像上次那樣」")] string Prompt,
    [property: Description("一次性排程的執行時間。ISO 8601 格式，建議含時區偏移（例如 2026-10-25T09:00:00+08:00）；不含偏移時視為 time_zone（省略就是本機時區）的當地時間（例如 2026-10-25T09:00）。必須晚於現在。與 recurrence 只能擇一")] string? At = null,
    [property: Description("週期排程的種類：daily（每天）、weekdays（週一至週五）、weekly（每週特定的星期，要搭配 days）、monthly（每月固定的日期，用 day_of_month）。與 at 只能擇一；使用時要搭配 time")] string? Recurrence = null,
    [property: Description("週期排程每次執行的時間，24 小時制 HH:mm（例如 09:00、18:30）。recurrence 有值時必填")] string? Time = null,
    [property: Description("weekly 用的星期幾，字串陣列，例如 [\"monday\", \"friday\"] 或 [\"1\", \"5\"]。英文星期不分大小寫；數字 1＝週一…7＝週日（0 也是週日）"), JsonConverter(typeof(LooseStringListConverter))] IReadOnlyList<string>? Days = null,
    [property: Description("monthly 用的每月日期，1～31，省略為 1。當月沒有那一天時（例如 2 月的 30 日）改用當月最後一天")] int? DayOfMonth = null,
    [property: Description("時區的 IANA 名稱，例如 Asia/Taipei、America/New_York；省略就是使用者電腦的時區")] string? TimeZone = null,
    [property: Description("模型建議的授權，字串陣列，只認 write（寫入檔案、筆記或外部服務）與 network（連到網站）。只填這個排程需要的，例如 [\"write\", \"network\"]；使用者會在確認卡上看到並調整。destructive 不要填，會被忽略"), JsonConverter(typeof(LooseStringListConverter))] IReadOnlyList<string>? SuggestedPermissions = null,
    [property: Description("由系統填入，不要自己填。使用者在確認卡上確認後實際勾選的授權，字串陣列，可以含 write、network、destructive"), JsonPropertyName("granted_permissions"), JsonConverter(typeof(LooseStringListConverter))] IReadOnlyList<string>? GrantedPermissions = null) : IReminderTimeArgs;

/// <summary>
/// 建立「定時自動跑一次 prompt」的排程（M24 4.4）。確認卡由 <see cref="Describe"/> 產生；
/// 授權與範圍在建立時一次決定，之後執行時不再詢問（工作範圍是建立當下這則對話加入的檔案與資料夾）。
/// </summary>
public sealed class CreateScheduledPromptTool(IReminderService reminders, ReminderOptions options, IModelCatalog catalog)
    : ToolBase<CreateScheduledPromptArgs>
{
    /// <summary>排程的紀錄保留筆數（給模型看的說明，與 <c>ScheduledRunService</c> 的上限一致）。</summary>
    private const int RecordLimit = 30;

    public override ToolDescriptor Descriptor { get; } = new(
        "create_scheduled_prompt", "建立排程",
        "建立一則定時自動做事的排程：到點時 Lumi 會用 prompt 自動跑一次，使用者不在電腦前，結果放在歷史對話的「排程」分頁。"
        + "只在使用者明確要求定時自動做事時使用（例如「每天早上幫我整理…」）；單純提醒用 create_reminder。"
        + "prompt 要寫成一次就能獨立完成的完整指令：排程執行時沒有人可以回答問題，也看不到這則對話；不要寫「像上次那樣」。"
        + "排程只會用這則對話目前加入的資料夾與檔案當工作範圍（沒有加入就不能存取檔案）。"
        + "suggested_permissions 只填這個排程需要的 write（寫入檔案、筆記或外部服務）與 network（連到網站）；使用者會在確認卡上看到並調整，不要填 destructive。"
        + "granted_permissions 由系統填入，不要自己填。"
        + "時間參數與 create_reminder 相同（一次性用 at，週期用 recurrence 加 time）。"
        + "Lumi 沒有在執行時不會觸發，錯過 2 小時內會補跑一次；超過就只記錄為錯過。不要承諾一定準時。設定後會把解析後的時間與授權顯示給使用者確認。",
        ToolPermission.Write, ToolModes.Workbench, TimeSpan.FromSeconds(10),
        Group: ToolGroups.Reminders, CanAlwaysAllow: false, AlwaysAsk: true,
        Summary: "在指定時間自動跑一次 prompt。");

    protected override ToolPreview Describe(CreateScheduledPromptArgs args, ToolContext context)
    {
        var target = string.IsNullOrWhiteSpace(args.Name) ? null : args.Name.Trim();
        if (!TryPlan(args, out var plan, out _))
            return new ToolPreview(target, null);

        var now = context.Time.GetUtcNow();
        var when = WhenText(plan.Trigger, plan.Zone);
        var next = NextRuns(plan.Trigger, plan.Zone, now);
        var detail = plan.Trigger is OneTimeTrigger oneTime
            ? $"{ReminderFormat.Local(oneTime.At, plan.Zone)} · {plan.Zone.Id}"
            : next.Count > 0
                ? $"{when}，接下來：{string.Join("、", next.Select(t => ReminderFormat.Local(t, plan.Zone)))}"
                : when;

        // 建議的授權：只認 write、network（破壞性不會被預先勾選）；已授權的 granted_permissions 在這裡一律忽略
        var suggested = ParseGrant(args.SuggestedPermissions, allowDestructive: false);
        var schedule = new ScheduleProposal(
            plan.Name,
            when,
            next.Count > 0 ? ReminderFormat.Local(next[0], plan.Zone) : null,
            plan.Prompt,
            ModelText(),
            context.Scope.Roots.Select(r => r.Name).ToList(),
            suggested.Write,
            suggested.Network);
        return new ToolPreview(target, detail, Schedule: schedule);
    }

    protected override async Task<ToolResult> InvokeAsync(CreateScheduledPromptArgs args, ToolContext context, CancellationToken ct)
    {
        if (!TryPlan(args, out var plan, out var error))
            return ToolResult.Error(error!, "參數不正確");

        // 授權：確認卡回傳的實際勾選為準（可含破壞性）；沒有（沒經過確認卡）就退回建議，且破壞性一律不給
        var suggested = ParseGrant(args.SuggestedPermissions, allowDestructive: false);
        var grant = args.GrantedPermissions is null
            ? suggested
            : ParseGrant(args.GrantedPermissions, allowDestructive: true);

        var scope = context.Scope.Roots
            .Select(r => new ScheduleScopeItem(r.IsDirectory ? ContextKind.Folder : ContextKind.File, r.Path, r.Name))
            .ToList();

        ScheduledItem item;
        try
        {
            item = await reminders.CreatePromptScheduleAsync(
                new NewPromptSchedule(plan.Name, plan.Prompt, plan.Trigger, plan.Zone.Id, grant, scope, ScheduledCreator.Agent), ct);
        }
        catch (ReminderException e)
        {
            // 業務錯誤（時間已過、達上限…）：訊息是給模型看的
            return ToolResult.Error(e.Message, "無法建立");
        }

        var next = item.NextFireAt;
        var when = WhenText(plan.Trigger, plan.Zone);
        var created = new CreatedSchedule(
            item.Id,
            item.Title,
            ReminderFormat.Iso(next, plan.Zone),
            next is { } value ? ReminderFormat.Local(value, plan.Zone) : null,
            ReminderFormat.GrantNames(grant),
            scope.Select(s => s.DisplayName).ToList(),
            PermissionNote(args, suggested, grant),
            $"紀錄在歷史對話的「排程」分頁，最多保留 {RecordLimit} 次。");
        return ToolResult.Ok(JsonSerializer.Serialize(created, ReminderFormat.JsonOptions), $"已建立排程 · {when}");
    }

    /// <summary>
    /// 共用的驗證：名稱、prompt、時區、時間。失敗時 <paramref name="error"/> 是給模型看的說明。
    /// </summary>
    private bool TryPlan(CreateScheduledPromptArgs args, out Plan plan, out string? error)
    {
        plan = null!;
        if (!ReminderFormat.TryCheckText(args.Name, "name", options.MaxTitleChars, out var name, out error))
            return false;
        if (!ReminderFormat.TryCheckText(args.Prompt, "prompt", options.MaxPromptChars, out var prompt, out error))
            return false;
        if (!ReminderFormat.TryResolveZone(args.TimeZone, out var zone, out error))
            return false;
        if (!ReminderParser.TryParseTrigger(args, zone, out var trigger, out error))
            return false;

        plan = new Plan(name, prompt, trigger!, zone);
        error = null;
        return true;
    }

    /// <summary>
    /// 授權解析：只認 write、network；<paramref name="allowDestructive"/> 為 false 時 destructive 一律忽略。
    /// 不分大小寫、忽略空白與無法辨識的字串。
    /// </summary>
    internal static ScheduleGrant ParseGrant(IEnumerable<string>? raw, bool allowDestructive)
    {
        var names = (raw ?? [])
            .Select(s => (s ?? "").Trim().ToLowerInvariant())
            .ToHashSet();
        return new ScheduleGrant(
            Write: names.Contains("write"),
            Network: names.Contains("network"),
            Destructive: allowDestructive && names.Contains("destructive"));
    }

    /// <summary>
    /// 實際授權與模型建議不同時，寫在結果裡讓模型看得到差異（不同就回說明，相同回 null）。
    /// </summary>
    private static string? PermissionNote(CreateScheduledPromptArgs args, ScheduleGrant suggested, ScheduleGrant actual)
    {
        var parts = new List<string>();
        var suggestedNames = ReminderFormat.GrantNames(suggested);
        var actualNames = ReminderFormat.GrantNames(actual);
        if (!suggestedNames.SequenceEqual(actualNames))
            parts.Add($"實際授權與建議不同：建議 {string.Join("、", suggestedNames)}，實際 {string.Join("、", actualNames)}。");

        var askedDestructive = (args.SuggestedPermissions ?? Enumerable.Empty<string>())
            .Any(s => string.Equals(s?.Trim(), "destructive", StringComparison.OrdinalIgnoreCase));
        if (askedDestructive && !actual.Destructive)
            parts.Add("建議中的 destructive 已忽略：破壞性操作需要使用者在確認卡上自己勾選。");

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    /// <summary>「每天 09:00」（週期）或「2026-10-25（週日）09:00（只一次）」（一次性）。</summary>
    private static string WhenText(Trigger trigger, TimeZoneInfo zone) => trigger switch
    {
        OneTimeTrigger oneTime => $"{ReminderFormat.Local(oneTime.At, zone)}（只一次）",
        RecurrenceTrigger recurrence => ScheduleCalculator.Describe(recurrence, zone.Id),
        _ => "",
    };

    private static IReadOnlyList<DateTimeOffset> NextRuns(Trigger trigger, TimeZoneInfo zone, DateTimeOffset now) => trigger switch
    {
        OneTimeTrigger oneTime => oneTime.At > now ? [oneTime.At] : [],
        RecurrenceTrigger => ScheduleCalculator.NextOccurrences(trigger, zone.Id, now, 3),
        _ => [],
    };

    /// <summary>確認卡的模型說明：工作台預設模型的「供應商 · model ID」；取不到就寫尚未設定。</summary>
    private string ModelText()
    {
        var settings = catalog.Current;
        var model = settings.WorkbenchModelId is { } id ? settings.Models.FirstOrDefault(m => m.Id == id) : null;
        var connection = model is null ? null : settings.Connections.FirstOrDefault(c => c.Id == model.ConnectionId);
        return model is null || connection is null
            ? "預設模型（尚未設定）"
            : $"預設模型（目前是 {connection.Name} · {model.ModelId}）";
    }

    private sealed record Plan(string Name, string Prompt, Trigger Trigger, TimeZoneInfo Zone);

    /// <summary>建立成功的回傳。<c>next_run_at</c> 是 ISO 8601，<c>next_run_at_local</c> 是給人看的時間。</summary>
    private sealed record CreatedSchedule(
        string Id,
        string Name,
        string? NextRunAt,
        string? NextRunAtLocal,
        IReadOnlyList<string> Granted,
        IReadOnlyList<string> Scope,
        string? PermissionNote,
        string Note);
}
