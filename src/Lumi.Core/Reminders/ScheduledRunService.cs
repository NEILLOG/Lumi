using System.Threading.Channels;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Prompts;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lumi.Core.Reminders;

/// <summary>
/// 排程跑 prompt 的執行器（M24，agent.md 第 4.2 節）。同時實作三個介面：
/// <see cref="IScheduledRunDispatcher"/>（<see cref="ReminderService"/> 把到點的項目交給它）、
/// <see cref="IScheduledRunHost"/>（權限分流用：暫停、讓位、加授權）、<see cref="IScheduledRunService"/>（畫面用）。
/// <para>
/// 佇列是 <see cref="Channel{T}"/>；每筆執行是一個背景工作，但<b>同時只有一個在呼叫 AI</b>（<see cref="_slot"/>）。
/// 待確認的執行會釋放位置，所以不擋後面的排程。執行中的對話不經過 <c>ChatSession</c>，不影響目前的對話。
/// </para>
/// </summary>
public sealed class ScheduledRunService(
    IScheduledRunStore runs,
    IScheduledItemStore items,
    ConversationService conversations,
    IConversationStore conversationStore,
    IConversationContextStore contexts,
    ConversationDeletionService deletion,
    INotificationSink sink,
    TimeProvider time,
    ScheduledRunOptions options,
    Func<IReminderService> reminders,
    ILogger<ScheduledRunService> logger) : IScheduledRunService, IScheduledRunDispatcher, IScheduledRunHost
{
    /// <summary>執行時發生未預期的錯誤：給使用者看的固定一句話，不洩漏內部細節。</summary>
    private const string UnexpectedFailureText = "執行時發生錯誤，沒有完成。";

    /// <summary>排程被刪除了，找不到要執行的內容。</summary>
    private const string MissingScheduleText = "排程已經不存在，沒有執行。";

    /// <summary>通知與紀錄列裡的一行摘要上限。</summary>
    private const int MaxSummaryChars = 100;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Dictionary<string, RunEntry> _entries = new(StringComparer.Ordinal);
    private readonly Channel<RunJob> _queue = Channel.CreateUnbounded<RunJob>(new UnboundedChannelOptions { SingleReader = true });

    public event EventHandler<ScheduledRun>? RunUpdated;

    public event EventHandler<ScheduledRunEventArgs>? EventReceived;

    /// <summary>
    /// 消費佇列直到 <paramref name="stopping"/> 被取消（App 的 hosted service 呼叫）。進行中的執行會被標為中斷，
    /// 這個方法等它們結束才回來。
    /// </summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        var inflight = new List<Task>();
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stopping))
            {
                inflight.RemoveAll(t => t.IsCompleted);
                inflight.Add(RunJobAsync(job, stopping));
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }

        await Task.WhenAll(inflight);

        // 還沒開始就停止的：不記紀錄（寧可漏跑，不重跑），只清掉登記
        while (_queue.Reader.TryRead(out var pending))
            lock (_lock) _entries.Remove(pending.Entry.RunId);
    }

    /// <summary>啟動時呼叫一次：執行中與待確認的紀錄改成中斷；當機殘留、沒有紀錄對應的對話刪掉。</summary>
    public async Task RecoverOnStartupAsync(CancellationToken ct)
    {
        var interrupted = await runs.MarkActiveInterruptedAsync(time.GetUtcNow(), ct);
        if (interrupted > 0)
            logger.LogInformation("將 {Count} 筆中斷的排程執行標為中斷", interrupted);

        foreach (var conversationId in await runs.ListOrphanRunConversationsAsync(ct))
        {
            try
            {
                await deletion.DeleteAsync(conversationId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "清除殘留的排程紀錄對話失敗");
            }
        }
    }

    // ---- IScheduledRunDispatcher（ReminderService 呼叫）----

    public void Enqueue(ScheduledItem item, DateTimeOffset plannedAt, ScheduledRunTrigger trigger) =>
        QueueRun(item, plannedAt, trigger);

    public async Task RecordMissedAsync(ScheduledItem item, DateTimeOffset plannedAt, CancellationToken ct)
    {
        var missed = new ScheduledRun(
            ScheduledRun.NewId(), item.Id, item.Title, null, plannedAt, null, null,
            ScheduledRunTrigger.Scheduled, ScheduledRunStatus.Missed, null, null);
        try
        {
            await runs.AddAsync(missed, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "記錄錯過的排程失敗（排程 {Schedule}）", item.Id);
            return;
        }

        OnRunUpdated(missed);
        await CleanupAsync(item.Id);
    }

    // ---- IScheduledRunService（畫面用）----

    public async Task<ScheduledRun?> RunNowAsync(string scheduleId, CancellationToken ct = default)
    {
        var item = await items.GetAsync(scheduleId, ct);
        if (item?.Action is not RunPromptAction)
            return null;

        var entry = QueueRun(item, time.GetUtcNow(), ScheduledRunTrigger.Manual);
        lock (_lock)
            return entry.Current;
    }

    public void Stop(string runId)
    {
        RunEntry? entry;
        lock (_lock)
            _entries.TryGetValue(runId, out entry);
        // 在鎖外取消：取消會同步執行註冊的回呼
        entry?.Cts.Cancel();
    }

    public bool IsActive(string conversationId) => FindEntryByConversation(conversationId) is not null;

    public async Task<bool> DeleteRunAsync(string runId, CancellationToken ct = default)
    {
        if (HasEntry(runId))
            return false;

        var run = await runs.GetAsync(runId, ct);
        if (run is null)
            return false;

        if (run.ConversationId is { } conversationId)
            await deletion.DeleteAsync(conversationId, ct);
        await runs.DeleteAsync(runId, ct);
        return true;
    }

    public async Task<int> DeleteRunsForScheduleAsync(string scheduleId, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var run in await runs.ListForScheduleAsync(scheduleId, ct))
        {
            if (HasEntry(run.Id))
                continue;

            if (run.ConversationId is { } conversationId)
                await deletion.DeleteAsync(conversationId, ct);
            await runs.DeleteAsync(run.Id, ct);
            count++;
        }
        return count;
    }

    public Task MarkReadAsync(string runId, CancellationToken ct = default) =>
        runs.MarkReadAsync(runId, time.GetUtcNow(), ct);

    // ---- IScheduledRunHost（權限分流用）----

    public ActiveScheduledRun? FindByConversation(string conversationId)
    {
        lock (_lock)
        {
            var entry = FindEntryByConversation(conversationId);
            return entry is null
                ? null
                : new ActiveScheduledRun(entry.RunId, entry.ScheduleId, entry.Current.ScheduleTitle, entry.Grant);
        }
    }

    public ToolApprovalRequest? GetPendingApproval(string runId)
    {
        lock (_lock)
            return _entries.TryGetValue(runId, out var entry) ? entry.PendingApproval : null;
    }

    public async Task<IAsyncDisposable> PauseForApprovalAsync(string conversationId, ToolApprovalRequest request, CancellationToken ct)
    {
        var entry = FindEntryByConversation(conversationId);
        if (entry is null)
            return new NoopResume();

        lock (_lock)
            entry.PendingApproval = request;
        await SaveRunAsync(entry, Snapshot(entry) with { Status = ScheduledRunStatus.AwaitingApproval });
        ReleaseSlot(entry);

        var current = Snapshot(entry);
        await NotifyAsync(new AppNotification(
            $"scheduled-run-{current.Id}-awaiting-{time.GetUtcNow().ToUnixTimeMilliseconds()}",
            $"『{current.ScheduleTitle}』需要你確認",
            request.DisplayName,
            time.GetUtcNow(),
            NotificationKind.ScheduledRun,
            new OpenScheduledRun(current.Id)));

        return new SlotResume(this, entry);
    }

    public async Task AddGrantAsync(string scheduleId, ToolPermission permission, CancellationToken ct)
    {
        var item = await items.GetAsync(scheduleId, ct);
        if (item?.Action is not RunPromptAction action)
            return;

        var grant = action.Grant.With(permission);
        await reminders().UpdatePromptScheduleAsync(scheduleId, new PromptScheduleUpdate(null, grant, null), ct);

        // 進行中的同一個排程也立即生效（之後同等級的工具不再詢問）
        lock (_lock)
        {
            foreach (var entry in _entries.Values.Where(e => e.ScheduleId == scheduleId))
                entry.Grant = entry.Grant.With(permission);
        }
    }

    // ---- 佇列與執行 ----

    private RunEntry QueueRun(ScheduledItem item, DateTimeOffset plannedAt, ScheduledRunTrigger trigger)
    {
        var run = new ScheduledRun(
            ScheduledRun.NewId(), item.Id, item.Title, null, plannedAt, null, null,
            trigger, ScheduledRunStatus.Running, null, null);
        var entry = new RunEntry(item.Id, new CancellationTokenSource(), run);
        lock (_lock)
            _entries[run.Id] = entry;
        _queue.Writer.TryWrite(new RunJob(entry, plannedAt, trigger));
        return entry;
    }

    private async Task RunJobAsync(RunJob job, CancellationToken stopping)
    {
        try
        {
            await ExecuteAsync(job, stopping);
        }
        catch (Exception e)
        {
            // ExecuteAsync 自己處理大部分的錯誤；這裡只是最後一道，避免未觀察的例外
            logger.LogError(e, "排程執行的背景工作發生未預期的錯誤（執行 {Run}）", job.Entry.RunId);
        }
    }

    private async Task ExecuteAsync(RunJob job, CancellationToken stopping)
    {
        var entry = job.Entry;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(entry.Cts.Token, stopping);
        var ct = linked.Token;
        SetToken(entry, ct);

        ScheduledRunStatus status;
        string? failure = null;
        string? answer = null;
        try
        {
            await SaveNewRunAsync(entry);

            // 同一個排程開始新的一次：前一次還在等確認的，視為拒絕並結束（M24 3.7）
            StopAwaitingRunsOf(entry);

            await _slot.WaitAsync(ct);
            SetSlot(entry, held: true);
            try
            {
                (answer, failure) = await ExecuteOnSlotAsync(entry, job, ct);
            }
            finally
            {
                ReleaseSlot(entry);
            }
            status = failure is null ? ScheduledRunStatus.Completed : ScheduledRunStatus.Failed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = ScheduledRunStatus.Interrupted;
            failure = null;
        }
        catch (Exception e)
        {
            logger.LogError(e, "排程 {Schedule} 的執行失敗", entry.ScheduleId);
            status = ScheduledRunStatus.Failed;
            failure = UnexpectedFailureText;
        }

        try
        {
            await FinishAsync(entry, status, failure, answer);
        }
        catch (Exception e)
        {
            logger.LogError(e, "寫入排程執行的結果失敗（執行 {Run}）", entry.RunId);
        }
        finally
        {
            lock (_lock)
                _entries.Remove(entry.RunId);
        }
    }

    /// <summary>拿到執行位置之後：讀排程的現況、建立對話、加入範圍、送出 prompt、消化事件。回傳最後的回答與失敗原因。</summary>
    private async Task<(string? Answer, string? Failure)> ExecuteOnSlotAsync(RunEntry entry, RunJob job, CancellationToken ct)
    {
        var item = await items.GetAsync(entry.ScheduleId, ct);
        if (item?.Action is not RunPromptAction action)
            return (null, MissingScheduleText);

        var started = time.GetUtcNow();
        var conversation = await conversationStore.CreateAsync(item.Title, ConversationKind.ScheduledRun, ct);
        lock (_lock)
            entry.Grant = action.Grant;
        await SaveRunAsync(entry, Snapshot(entry) with
        {
            ConversationId = conversation.Id,
            StartedAt = started,
            Status = ScheduledRunStatus.Running,
        });

        await AddScopeAsync(conversation.Id, entry.RunId, action.Scope, ct);

        var info = new ScheduledRunInfo(item.Title, job.PlannedAt, started);
        string? answer = null;
        string? failure = null;
        await foreach (var chatEvent in conversations.SendAsync(conversation.Id, action.Prompt, ct, ChatMode.Workbench, new ConversationRunOptions(info)))
        {
            RaiseEvent(entry.RunId, conversation.Id, chatEvent);
            if (chatEvent is Completed completed && LastText(completed.Message) is { } text)
                answer = text;
            else if (chatEvent is Failed failed)
                failure = failed.Failure.Message;
        }
        return (answer, failure);
    }

    /// <summary>
    /// 把排程範圍的檔案與資料夾加進這次的對話，並立刻標成已送出：第一則訊息不帶資料夾的快照，模型要看再用工具讀（Q1）。
    /// <c>embedded_in_message_id</c> 沒有外鍵，所以用這次執行的識別字當標記，不必找一則真的訊息。
    /// </summary>
    private async Task AddScopeAsync(string conversationId, string runId, IReadOnlyList<ScheduleScopeItem> scope, CancellationToken ct)
    {
        if (scope.Count == 0)
            return;

        var ids = new List<string>(scope.Count);
        foreach (var item in scope)
        {
            var added = await contexts.AddAsync(conversationId, item.Kind, item.Path, item.DisplayName, ct);
            ids.Add(added.Id);
        }
        await contexts.MarkEmbeddedAsync(conversationId, ids, $"scheduled-scope:{runId}", CancellationToken.None);
    }

    /// <summary>執行結束（完成、失敗、中斷）：寫入狀態、發事件、送通知、清理保留上限外的舊紀錄。</summary>
    private async Task FinishAsync(RunEntry entry, ScheduledRunStatus status, string? failure, string? answer)
    {
        var now = time.GetUtcNow();
        var finished = Snapshot(entry) with { Status = status, FinishedAt = now, FailureText = failure };
        await SaveRunAsync(entry, finished);

        switch (status)
        {
            case ScheduledRunStatus.Completed:
                await NotifyAsync(new AppNotification(
                    $"scheduled-run-{finished.Id}-completed",
                    $"『{finished.ScheduleTitle}』完成了",
                    Summary(answer),
                    now,
                    NotificationKind.ScheduledRun,
                    new OpenScheduledRun(finished.Id)));
                break;
            case ScheduledRunStatus.Failed:
                await NotifyAsync(new AppNotification(
                    $"scheduled-run-{finished.Id}-failed",
                    $"『{finished.ScheduleTitle}』沒有完成",
                    Summary(failure),
                    now,
                    NotificationKind.ScheduledRun,
                    new OpenScheduledRun(finished.Id)));
                break;
        }

        await CleanupAsync(entry.ScheduleId);
    }

    /// <summary>交還執行位置；只有持有的時候才真的釋放。</summary>
    private void ReleaseSlot(RunEntry entry)
    {
        lock (_lock)
        {
            if (!entry.HoldsSlot)
                return;
            entry.HoldsSlot = false;
        }
        _slot.Release();
    }

    /// <summary>待確認的回答到了：重新排隊取得執行位置，run 改回執行中。已停止的就不再取得。</summary>
    private async Task ResumeAsync(RunEntry entry)
    {
        CancellationToken token;
        lock (_lock)
        {
            token = entry.Token;
            entry.PendingApproval = null;
        }
        if (token.IsCancellationRequested)
            return;

        try
        {
            await _slot.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        SetSlot(entry, held: true);
        await SaveRunAsync(entry, Snapshot(entry) with { Status = ScheduledRunStatus.Running });
    }

    /// <summary>同一個排程的其他進行中執行，若正在等確認，全部取消（會被標為中斷）。</summary>
    private void StopAwaitingRunsOf(RunEntry entry)
    {
        List<RunEntry> targets;
        lock (_lock)
        {
            targets = _entries.Values
                .Where(e => !ReferenceEquals(e, entry) && e.ScheduleId == entry.ScheduleId && e.Current.Status == ScheduledRunStatus.AwaitingApproval)
                .ToList();
        }
        foreach (var target in targets)
            target.Cts.Cancel();
    }

    // ---- 紀錄的寫入與通知 ----

    private async Task SaveNewRunAsync(RunEntry entry)
    {
        await runs.AddAsync(Snapshot(entry), CancellationToken.None);
        OnRunUpdated(Snapshot(entry));
    }

    private async Task SaveRunAsync(RunEntry entry, ScheduledRun next)
    {
        lock (_lock)
            entry.Current = next;
        await runs.UpdateAsync(next, CancellationToken.None);
        OnRunUpdated(next);
    }

    /// <summary>保留上限：同一個排程超過 <see cref="ScheduledRunOptions.KeepRunsPerSchedule"/> 的最舊紀錄，連同對話刪除。失敗只記 log。</summary>
    private async Task CleanupAsync(string scheduleId)
    {
        try
        {
            foreach (var run in await runs.ListBeyondAsync(scheduleId, options.KeepRunsPerSchedule, CancellationToken.None))
            {
                if (HasEntry(run.Id))
                    continue;

                if (run.ConversationId is { } conversationId)
                    await deletion.DeleteAsync(conversationId, CancellationToken.None);
                await runs.DeleteAsync(run.Id, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "清理排程 {Schedule} 的舊紀錄失敗", scheduleId);
        }
    }

    private void OnRunUpdated(ScheduledRun run)
    {
        try
        {
            RunUpdated?.Invoke(this, run);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "通知畫面排程紀錄更新時發生錯誤");
        }
    }

    private void RaiseEvent(string runId, string conversationId, ChatEvent chatEvent)
    {
        try
        {
            EventReceived?.Invoke(this, new ScheduledRunEventArgs(runId, conversationId, chatEvent));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "通知畫面排程事件時發生錯誤");
        }
    }

    private async Task NotifyAsync(AppNotification notification)
    {
        try
        {
            await sink.ShowAsync(notification, CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 狀態已經寫入，通知失敗只記 log
            logger.LogWarning(e, "顯示排程通知失敗（通知 {Id}）", notification.Id);
        }
    }

    // ---- 小工具 ----

    private RunEntry? FindEntryByConversation(string conversationId)
    {
        lock (_lock)
            return _entries.Values.FirstOrDefault(e => e.Current.ConversationId == conversationId);
    }

    private bool HasEntry(string runId)
    {
        lock (_lock)
            return _entries.ContainsKey(runId);
    }

    private ScheduledRun Snapshot(RunEntry entry)
    {
        lock (_lock)
            return entry.Current;
    }

    private void SetToken(RunEntry entry, CancellationToken token)
    {
        lock (_lock)
            entry.Token = token;
    }

    private void SetSlot(RunEntry entry, bool held)
    {
        lock (_lock)
            entry.HoldsSlot = held;
    }

    /// <summary>摘要：第一行，超過 <see cref="MaxSummaryChars"/> 字截斷並加「…」。</summary>
    private static string Summary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= MaxSummaryChars ? line : line[..MaxSummaryChars] + "…";
    }

    /// <summary>最後一則助理訊息的文字；沒有文字（只有工具呼叫）回 null。</summary>
    private static string? LastText(ChatMessage message)
    {
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text)).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>排程的一次執行。<see cref="Current"/> 是最新的紀錄；所有欄位只在 <c>_lock</c> 底下存取。</summary>
    private sealed class RunEntry(string scheduleId, CancellationTokenSource cts, ScheduledRun current)
    {
        public string ScheduleId { get; } = scheduleId;

        public CancellationTokenSource Cts { get; } = cts;

        public ScheduledRun Current { get; set; } = current;

        /// <summary>正在等使用者確認的請求（待確認時）；回答後清掉。</summary>
        public ToolApprovalRequest? PendingApproval { get; set; }

        public string RunId => Current.Id;

        /// <summary>這次執行的連結權杖（含停止與程序結束）；等待確認時用來判斷是否已經停止。</summary>
        public CancellationToken Token { get; set; }

        /// <summary>這次執行開始時從排程讀到的授權；「以後都允許」會更新它。</summary>
        public ScheduleGrant Grant { get; set; } = ScheduleGrant.None;

        /// <summary>目前持有執行位置（<see cref="ScheduledRunService._slot"/>）。</summary>
        public bool HoldsSlot { get; set; }
    }

    private sealed record RunJob(RunEntry Entry, DateTimeOffset PlannedAt, ScheduledRunTrigger Trigger);

    /// <summary>待確認結束時交還執行位置：<see cref="DisposeAsync"/> 重新排隊取得位置。只會生效一次。</summary>
    private sealed class SlotResume(ScheduledRunService owner, RunEntry entry) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1)
                return ValueTask.CompletedTask;
            return new ValueTask(owner.ResumeAsync(entry));
        }
    }

    /// <summary>沒有對應的進行中執行時的空操作。</summary>
    private sealed class NoopResume : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
