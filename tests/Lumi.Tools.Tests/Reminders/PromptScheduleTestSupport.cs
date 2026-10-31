using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Tools.Tests.Reminders;

/// <summary>
/// 假的 <see cref="IReminderService"/>（M24 24D）：建立「跑 prompt」排程時不經過 24B 的實作，只記錄請求，
/// 並把項目寫進 <see cref="ReminderRig"/> 的記憶體儲存，讓列出、取消、啟停的工具可以接著用真的服務測試。
/// 其他方法直接委派給真的 <see cref="ReminderService"/>。
/// </summary>
internal sealed class FakePromptReminderService(ReminderRig rig) : IReminderService
{
    /// <summary>收到的建立請求（依順序）。</summary>
    public List<NewPromptSchedule> Requests { get; } = [];

    /// <summary>設成值時，建立就丟這個例外（模擬達上限、時間已過等業務錯誤）。</summary>
    public ReminderException? Fail { get; set; }

    public async Task<ScheduledItem> CreatePromptScheduleAsync(NewPromptSchedule request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Fail is { } fail)
            throw fail;

        var now = rig.Clock.GetUtcNow();
        var zone = request.TimeZoneId ?? TimeZoneInfo.Local.Id;
        var next = request.Trigger is OneTimeTrigger oneTime
            ? oneTime.At
            : ScheduleCalculator.NextOccurrence(request.Trigger, zone, now);
        var item = new ScheduledItem(
            ScheduledItem.NewId(),
            request.Trigger is RecurrenceTrigger ? ScheduledKind.Schedule : ScheduledKind.Reminder,
            request.Name,
            request.Prompt,
            request.Trigger,
            zone,
            new RunPromptAction(request.Prompt, request.Grant, request.Scope),
            ScheduledStatus.Pending,
            true,
            next,
            null,
            request.CreatedBy,
            now);
        await rig.Store.AddAsync(item, ct);
        return item;
    }

    public Task<ScheduledItem> CreateReminderAsync(NewReminder request, CancellationToken ct) =>
        rig.Service.CreateReminderAsync(request, ct);

    public Task<ScheduledItem> StartTimerAsync(string? label, int durationSeconds, CancellationToken ct) =>
        rig.Service.StartTimerAsync(label, durationSeconds, ct);

    public Task<ScheduledItem?> UpdatePromptScheduleAsync(string id, PromptScheduleUpdate update, CancellationToken ct) =>
        throw new NotSupportedException("測試不會修改排程。");

    public Task<IReadOnlyList<ScheduledItem>> ListAsync(ReminderQuery query, CancellationToken ct) =>
        rig.Service.ListAsync(query, ct);

    public Task<CancelResult> CancelAsync(string id, CancellationToken ct) => rig.Service.CancelAsync(id, ct);

    public Task<ScheduledItem?> SetEnabledAsync(string id, bool enabled, CancellationToken ct) =>
        rig.Service.SetEnabledAsync(id, enabled, ct);

    public Task<bool> DeleteAsync(string id, CancellationToken ct) => rig.Service.DeleteAsync(id, ct);

    public Task<int> DeleteFinishedAsync(CancellationToken ct) => rig.Service.DeleteFinishedAsync(ct);
}

/// <summary>只有根清單有意義的假範圍：不碰磁碟，路徑解析一律拒絕（工具只需要 <see cref="IFileScope.Roots"/>）。</summary>
internal sealed class FakeFileScope(params ScopeRoot[] roots) : IFileScope
{
    public IReadOnlyList<ScopeRoot> Roots { get; } = roots;

    public ScopeResolution Resolve(string path) => ScopeResolution.Denied("測試範圍不解析路徑。");

    public string ToDisplayPath(string fullPath) => fullPath;
}
