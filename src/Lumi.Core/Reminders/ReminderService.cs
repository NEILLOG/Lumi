using Microsoft.Extensions.Logging;

namespace Lumi.Core.Reminders;

/// <summary>
/// 提醒與排程的核心。<see cref="IReminderService"/> 給工具呼叫；<see cref="ProcessDueAsync"/>、<see cref="OnStartupAsync"/>、
/// <see cref="GetWaitAsync"/> 給 App 的背景迴圈呼叫（迴圈的寫法見 App 的 ReminderHostedService）。
/// 所有「讀－改－寫」都在同一把 <see cref="SemaphoreSlim"/> 底下；通知在狀態寫入之後才送出（寧可漏響，不要重響）。
/// 時間一律來自注入的 <see cref="TimeProvider"/>。
/// </summary>
public sealed class ReminderService(
    IScheduledItemStore store,
    INotificationSink sink,
    TimeProvider time,
    ReminderOptions options,
    ILogger<ReminderService> logger) : IReminderService
{
    /// <summary>產生不重複的 Id 最多試幾次（隨機值，碰撞極少見）。</summary>
    private const int MaxIdAttempts = 5;

    /// <summary>錯過的彙總最多列出幾個標題。</summary>
    private const int MaxListedMissed = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _changeLock = new();
    private CancellationTokenSource _changes = new();

    /// <summary>
    /// 變更訊號：建立、取消、啟停之後換成新的權杖，並取消舊的，讓背景迴圈立刻重新算等待時間。
    /// 呼叫端要<b>先</b>取得它、再讀取等待時間，這樣兩者之間發生的變更不會漏掉。
    /// </summary>
    public CancellationToken ChangeToken
    {
        get
        {
            lock (_changeLock)
                return _changes.Token;
        }
    }

    public async Task<ScheduledItem> CreateReminderAsync(NewReminder request, CancellationToken ct)
    {
        var title = ValidateText(request.Title, options.MaxTitleChars, "標題");
        var message = ValidateText(request.Message, options.MaxMessageChars, "內容");
        var zoneId = ScheduleCalculator.ResolveTimeZone(request.TimeZoneId ?? string.Empty).Id;
        var now = time.GetUtcNow();
        var nextFireAt = FirstFireAt(request.Trigger, zoneId, now);

        ScheduledItem item;
        await _gate.WaitAsync(ct);
        try
        {
            await EnsurePendingCapacityAsync(ct);
            item = await AddAsNewItemAsync(id => new ScheduledItem(
                Id: id,
                Kind: request.Kind,
                Title: title,
                Message: message,
                Trigger: request.Trigger,
                TimeZoneId: zoneId,
                Action: new NotifyAction(),
                Status: ScheduledStatus.Pending,
                Enabled: true,
                NextFireAt: nextFireAt,
                LastFiredAt: null,
                CreatedBy: request.CreatedBy,
                CreatedAt: now), ct);
        }
        finally
        {
            _gate.Release();
        }

        SignalChanged();
        return item;
    }

    public async Task<ScheduledItem> StartTimerAsync(string? label, int durationSeconds, CancellationToken ct)
    {
        if (durationSeconds < options.MinTimerSeconds || durationSeconds > options.MaxTimerSeconds)
            throw new ReminderException(
                $"durationSeconds 必須介於 {options.MinTimerSeconds} 到 {options.MaxTimerSeconds} 秒 ({options.MaxTimerSeconds / 3600} 小時) 之間。");

        var name = string.IsNullOrWhiteSpace(label) ? "計時器" : label.Trim();
        if (name.Length > options.MaxTitleChars)
            throw new ReminderException($"計時器名稱不能超過 {options.MaxTitleChars} 字。");

        var request = new NewReminder(
            Title: name,
            Message: $"「{name}」時間到了。",
            Trigger: new OneTimeTrigger(time.GetUtcNow().AddSeconds(durationSeconds)),
            TimeZoneId: null,
            Kind: ScheduledKind.Timer,
            CreatedBy: ScheduledCreator.Agent);
        return await CreateReminderAsync(request, ct);
    }

    public async Task<IReadOnlyList<ScheduledItem>> ListAsync(ReminderQuery query, CancellationToken ct)
    {
        if (query.Limit < 1)
            throw new ReminderException("筆數必須至少是 1。");
        return await store.ListAsync(new ScheduledItemFilter(query.Statuses, query.Kinds, query.Limit), ct);
    }

    public async Task<CancelResult> CancelAsync(string id, CancellationToken ct)
    {
        CancelResult result;
        await _gate.WaitAsync(ct);
        try
        {
            var item = await store.GetAsync(id, ct);
            if (item is null)
                return new CancelResult(null, false);
            if (IsFinished(item.Status))
                return new CancelResult(item, false);

            var cancelled = item with { Status = ScheduledStatus.Cancelled };
            if (!await store.UpdateAsync(cancelled, ct))
                return new CancelResult(null, false);
            result = new CancelResult(cancelled, true);
        }
        finally
        {
            _gate.Release();
        }

        SignalChanged();
        return result;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        bool deleted;
        await _gate.WaitAsync(ct);
        try
        {
            deleted = await store.DeleteAsync(id, ct);
        }
        finally
        {
            _gate.Release();
        }

        if (deleted)
            SignalChanged();
        return deleted;
    }

    public async Task<int> DeleteFinishedAsync(CancellationToken ct)
    {
        int deleted;
        await _gate.WaitAsync(ct);
        try
        {
            deleted = await store.DeleteFinishedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }

        if (deleted > 0)
            SignalChanged();
        return deleted;
    }

    public async Task<ScheduledItem?> SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        ScheduledItem updated;
        await _gate.WaitAsync(ct);
        try
        {
            var item = await store.GetAsync(id, ct);
            if (item is null)
                return null;
            if (item.Status != ScheduledStatus.Pending)
                throw new ReminderException("這個項目已經結束，無法啟用或停用。");

            if (enabled)
            {
                // 啟用時重新算下一次：停用期間可能已經過了原來的時間
                var next = ScheduleCalculator.NextOccurrence(item.Trigger, item.TimeZoneId, time.GetUtcNow())
                    ?? throw new ReminderException(item.Trigger is OneTimeTrigger
                        ? "這個提醒的時間已經過了，無法啟用。"
                        : "這個週期算不出下一次的時間，無法啟用。");
                updated = item with { Enabled = true, NextFireAt = next };
            }
            else
            {
                updated = item with { Enabled = false };
            }

            await store.UpdateAsync(updated, ct);
        }
        finally
        {
            _gate.Release();
        }

        SignalChanged();
        return updated;
    }

    /// <summary>
    /// 到點的項目：一次性標為完成，週期推進到下一次。每筆都是「先寫入、再通知」；通知失敗只記 log。
    /// </summary>
    public async Task ProcessDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var due in await store.GetDueAsync(now, ct))
        {
            var claim = await ClaimAsync(due.Id, now, startup: false, ct);
            if (claim is not null)
                await NotifyAsync(ReminderNotification(claim.Item, now), ct);
        }
    }

    /// <summary>
    /// 啟動與睡眠喚醒後呼叫（語意相同）。錯過超過寬限的一次性項目標為 <see cref="ScheduledStatus.Missed"/>、不響，
    /// 彙總成一則通知；寬限內的照常響；週期項目補一次並推進到未來。最後清除已結束超過保留期的項目。
    /// </summary>
    public async Task OnStartupAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var missed = new List<ScheduledItem>();
        foreach (var due in await store.GetDueAsync(now, ct))
        {
            var claim = await ClaimAsync(due.Id, now, startup: true, ct);
            if (claim is null)
                continue;
            if (claim.Missed)
                missed.Add(claim.Item);
            else
                await NotifyAsync(ReminderNotification(claim.Item, now), ct);
        }

        if (missed.Count > 0)
            await NotifyAsync(MissedSummary(missed, now), ct);

        await _gate.WaitAsync(ct);
        try
        {
            var purged = await store.PurgeFinishedAsync(now - options.FinishedRetention, ct);
            if (purged > 0)
                logger.LogInformation("清除 {Count} 筆已結束超過保留期的提醒", purged);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 距離下一次要處理的時間：沒有待觸發的項目回 null（迴圈不排計時器）；否則為「到最近一筆」與 <see cref="ReminderOptions.MaxWait"/> 中較短者，最小 0。
    /// </summary>
    public async Task<TimeSpan?> GetWaitAsync(CancellationToken ct)
    {
        var next = await store.GetNextFireAtAsync(ct);
        if (next is not { } fireAt)
            return null;

        var wait = fireAt - time.GetUtcNow();
        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;
        return wait < options.MaxWait ? wait : options.MaxWait;
    }

    /// <summary>
    /// 在鎖內重新讀取項目，確認仍然到點（之間可能被取消或停用），再寫入新狀態。
    /// 回傳寫入後的項目；沒有需要處理時回 null。<paramref name="startup"/> 為 true 時才套用錯過的規則。
    /// </summary>
    private async Task<Claim?> ClaimAsync(string id, DateTimeOffset now, bool startup, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await store.GetAsync(id, ct);
            if (current is null || !IsDue(current, now))
                return null;

            var missed = startup
                && current.Trigger is OneTimeTrigger
                && now - current.NextFireAt!.Value > options.MissedGrace;
            var updated = missed ? current with { Status = ScheduledStatus.Missed } : Advance(current, now);
            if (!await store.UpdateAsync(updated, ct))
                return null;
            return new Claim(updated, missed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>觸發之後的狀態：一次性完成；週期推進到 <paramref name="now"/> 之後的下一次（算不出來就完成）。</summary>
    private static ScheduledItem Advance(ScheduledItem item, DateTimeOffset now)
    {
        if (item.Trigger is OneTimeTrigger)
            return item with { Status = ScheduledStatus.Completed, LastFiredAt = now };

        var next = ScheduleCalculator.NextOccurrence(item.Trigger, item.TimeZoneId, now);
        return item with
        {
            LastFiredAt = now,
            NextFireAt = next,
            Status = next is null ? ScheduledStatus.Completed : ScheduledStatus.Pending,
        };
    }

    private async Task NotifyAsync(AppNotification notification, CancellationToken ct)
    {
        try
        {
            await sink.ShowAsync(notification, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 顯示失敗不影響使用：狀態已經寫入，不會重響
            logger.LogWarning(e, "顯示提醒通知失敗（通知 {Id}）", notification.Id);
        }
    }

    private static AppNotification ReminderNotification(ScheduledItem item, DateTimeOffset now) =>
        new($"{item.Id}-{now.ToUnixTimeMilliseconds()}", item.Title, item.Message, now, NotificationKind.Reminder);

    private AppNotification MissedSummary(IReadOnlyList<ScheduledItem> missed, DateTimeOffset now)
    {
        var lines = missed.Take(MaxListedMissed).Select(item => $"・{item.Title}").ToList();
        if (missed.Count > lines.Count)
            lines.Add($"…另外還有 {missed.Count - lines.Count} 個");

        return new AppNotification(
            $"missed-{now.ToUnixTimeMilliseconds()}",
            $"你錯過了 {missed.Count} 個提醒",
            string.Join("\n", lines),
            now,
            NotificationKind.MissedSummary);
    }

    private async Task EnsurePendingCapacityAsync(CancellationToken ct)
    {
        if (await store.CountPendingAsync(ct) >= options.MaxPendingItems)
            throw new ReminderException($"未完成排程項目已達上限 ({options.MaxPendingItems} 筆)。");
    }

    /// <summary>用新產生的 Id 寫入；只有 Id 已經存在時才換一個重試，其他錯誤照常丟出。</summary>
    private async Task<ScheduledItem> AddAsNewItemAsync(Func<string, ScheduledItem> build, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var item = build(ScheduledItem.NewId());
            try
            {
                await store.AddAsync(item, ct);
                return item;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (await store.GetAsync(item.Id, ct) is null)
                    throw;
                if (attempt >= MaxIdAttempts)
                    throw new ReminderException("無法產生不重複的編號，請再試一次。");
                logger.LogWarning(e, "提醒編號重複，重新產生（第 {Attempt} 次）", attempt);
            }
        }
    }

    /// <summary>建立時的第一次觸發時間；一次性已過期、或週期算不出下一次，都是業務錯誤。</summary>
    private static DateTimeOffset FirstFireAt(Trigger trigger, string zoneId, DateTimeOffset now)
    {
        if (trigger is RecurrenceTrigger { Kind: RecurrenceKind.Monthly, DayOfMonth: { } day } && (day < 1 || day > 31))
            throw new ReminderException("每月的日期必須介於 1 到 31 之間。");

        var next = ScheduleCalculator.NextOccurrence(trigger, zoneId, now);
        if (next is not null)
            return next.Value;

        throw trigger is OneTimeTrigger
            ? new ReminderException("提醒時間不能是過去的時間。")
            : new ReminderException("這個週期算不出下一次的時間，請確認日期或星期。");
    }

    private static string ValidateText(string text, int maxChars, string label)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            throw new ReminderException($"{label}不能是空的。");
        if (trimmed.Length > maxChars)
            throw new ReminderException($"{label}不能超過 {maxChars} 字。");
        return trimmed;
    }

    private static bool IsDue(ScheduledItem item, DateTimeOffset now) =>
        item.Enabled && item.Status == ScheduledStatus.Pending && item.NextFireAt is { } fireAt && fireAt <= now;

    private static bool IsFinished(ScheduledStatus status) =>
        status is ScheduledStatus.Completed or ScheduledStatus.Cancelled or ScheduledStatus.Missed;

    private void SignalChanged()
    {
        CancellationTokenSource previous;
        lock (_changeLock)
        {
            previous = _changes;
            _changes = new CancellationTokenSource();
        }
        previous.Cancel();
    }

    /// <summary>寫入後的項目；<see cref="Missed"/> 表示這次是錯過（不響）。</summary>
    private sealed record Claim(ScheduledItem Item, bool Missed);
}
