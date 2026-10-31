using System.Runtime.CompilerServices;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Prompts;
using Lumi.Core.Reminders;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Reminders;

/// <summary>
/// 排程跑 prompt 的執行器（M24 4.2、4.3、4.8）：到點執行、佇列、補跑與錯過、停止、保留上限、權限暫停與讓位、刪除與清理。
/// 假 provider 照 <see cref="ConversationToolLoopTests"/> 的寫法；時間用 <see cref="ManualClock"/>。
/// </summary>
public sealed class ScheduledRunServiceTests
{
    private const string Prompt = "把三個網站的更新整理成筆記。";

    /// <summary>預設的保留上限（<see cref="ScheduledRunOptions.KeepRunsPerSchedule"/>）。</summary>
    private const int DefaultKeep = 30;

    private static DateTimeOffset T(string text) => ReminderTestData.T(text);

    private static readonly DateTimeOffset NineAm = T("2026-10-23T09:00+08:00");

    private static ToolUseBlock Use(string id, string name) => new(id, name, """{"text":"x"}""");

    private static ChatEvent[] Answer(string text) =>
        [new TextDelta(text), new Completed(ScriptedChatProvider.Assistant(new TextBlock(text)), StopReason.EndTurn)];

    private static ChatEvent[] ToolCalls(params ToolUseBlock[] uses) =>
        [new Completed(ScriptedChatProvider.Assistant(uses), StopReason.ToolUse)];

    private static string LastUserText(ChatRequest request) =>
        string.Concat(request.Messages.Last(m => m.Role == ChatRole.User).Content.OfType<TextBlock>().Select(b => b.Text));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (await condition())
                return;
            await Task.Delay(5);
        }
        throw new TimeoutException($"等不到：{what}");
    }

    /// <summary>回答固定文字，並記下每次請求與呼叫次數。</summary>
    private sealed class AnswerProvider(string text = "好") : IChatProvider
    {
        private readonly List<ChatRequest> _requests = [];

        public int Calls;

        public List<ChatRequest> Requests
        {
            get
            {
                lock (_requests)
                    return _requests.ToList();
            }
        }

        public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            lock (_requests)
                _requests.Add(request);
            await Task.Yield();
            foreach (var chatEvent in Answer(text))
                yield return chatEvent;
        }
    }

    /// <summary>依請求決定回應的假 provider；<see cref="Script"/> 可以拋例外、回工具呼叫或文字。記錄同時在呼叫中的最大數。</summary>
    private sealed class ScriptProvider(Func<ChatRequest, ChatEvent[]> script, Func<ChatRequest, Task>? before = null) : IChatProvider
    {
        private readonly object _lock = new();
        private int _inFlight;

        public int Calls;

        public int MaxInFlight { get; private set; }

        public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            var now = Interlocked.Increment(ref _inFlight);
            lock (_lock)
                MaxInFlight = Math.Max(MaxInFlight, now);
            try
            {
                if (before is not null)
                    await before(request);
                await Task.Yield();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }

            foreach (var chatEvent in script(request))
                yield return chatEvent;
        }
    }

    /// <summary>需要確認的工具：把請求交給執行器暫停，等測試釋放 <see cref="Gate"/>。</summary>
    private sealed class PausingApprovals : IToolApprovalService
    {
        public IScheduledRunHost? Host { get; set; }

        /// <summary>目前要暫停的對話（測試只有一則進行中的排程對話）。</summary>
        public Func<string?> Lookup { get; set; } = () => null;

        public TaskCompletionSource<ApprovalDecision> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ToolApprovalRequest> Requests { get; } = [];

        public async Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            await using var resume = await Host!.PauseForApprovalAsync(Lookup()!, request, ct);
            return await Gate.Task.WaitAsync(ct);
        }
    }

    private sealed class NoAttachmentReferences : IAttachmentReferences
    {
        public Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    /// <summary>組好的執行器：記憶體儲存、手動時鐘、同一個通知接收器；背景迴圈在建立時啟動，<see cref="DisposeAsync"/> 停止。</summary>
    private sealed class RunHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public RunHarness(
            IChatProvider provider, FakeModelCatalog? catalog = null, IEnumerable<ITool>? tools = null,
            IToolApprovalService? approvals = null, ScheduledRunOptions? runOptions = null, DateTimeOffset? now = null,
            IPromptComposer? composer = null)
        {
            Clock = new ManualClock(now ?? ReminderTestData.Start);
            Items = new InMemoryScheduledItemStore();
            Runs = new InMemoryScheduledRunStore();
            Conversations = new InMemoryConversationStore();
            Contexts = new InMemoryConversationContextStore();
            Memory = new ToolPermissionMemory();
            Sink = new RecordingNotificationSink();
            Deletion = new ConversationDeletionService(Conversations, Memory,
                new AttachmentCleaner(new NullAttachmentStore(), new NoAttachmentReferences(), NullLogger<AttachmentCleaner>.Instance));
            Provider = provider;
            Chat = new ConversationService(provider, Conversations, catalog ?? new FakeModelCatalog(), new ToolRegistry(tools ?? []),
                approvals ?? new ScriptedApprovalService(), Memory, new ToolLoopOptions(), TimeProvider.System,
                NullLogger<ConversationService>.Instance, Contexts, composer: composer);
            Runner = new ScheduledRunService(Runs, Items, Chat, Conversations, Contexts, Deletion, Sink, Clock,
                runOptions ?? new ScheduledRunOptions(), () => Reminders, NullLogger<ScheduledRunService>.Instance);
            Reminders = new ReminderService(Items, Sink, Clock, new ReminderOptions(), NullLogger<ReminderService>.Instance, Runner);
            _loop = Runner.RunAsync(_stop.Token);
        }

        public ManualClock Clock { get; }

        public InMemoryScheduledItemStore Items { get; }

        public InMemoryScheduledRunStore Runs { get; }

        public InMemoryConversationStore Conversations { get; }

        public InMemoryConversationContextStore Contexts { get; }

        public ToolPermissionMemory Memory { get; }

        public RecordingNotificationSink Sink { get; }

        public ConversationDeletionService Deletion { get; }

        public IChatProvider Provider { get; }

        public ConversationService Chat { get; }

        public ScheduledRunService Runner { get; }

        public ReminderService Reminders { get; private set; } = null!;

        public async Task<ScheduledItem> CreatePromptAsync(
            string name = "整理更新", string prompt = Prompt, ScheduleGrant? grant = null,
            IReadOnlyList<ScheduleScopeItem>? scope = null, Trigger? trigger = null)
        {
            return await Reminders.CreatePromptScheduleAsync(new NewPromptSchedule(
                name, prompt, trigger ?? new OneTimeTrigger(NineAm), "Asia/Taipei",
                grant ?? ScheduleGrant.None, scope ?? [], ScheduledCreator.Agent), default);
        }

        /// <summary>等某個排程的紀錄達到指定狀態，回傳那一筆（由新到舊的第一筆符合者）。</summary>
        public async Task<ScheduledRun> WaitForRunAsync(string scheduleId, ScheduledRunStatus status)
        {
            ScheduledRun? found = null;
            await WaitUntilAsync(async () =>
            {
                found = (await Runs.ListForScheduleAsync(scheduleId)).FirstOrDefault(r => r.Status == status);
                return found is not null;
            }, $"排程 {scheduleId} 的紀錄變成 {status}");
            return found!;
        }

        public async Task WaitForRunStatusByIdAsync(string runId, ScheduledRunStatus status) =>
            await WaitUntilAsync(async () => (await Runs.GetAsync(runId))?.Status == status, $"紀錄 {runId} 變成 {status}");

        public async Task WaitForNotificationAsync(NotificationKind kind, string? title = null) =>
            await WaitUntilAsync(() => Task.FromResult(Sink.Shown.Any(n => n.Kind == kind && (title is null || n.Title == title))), $"通知 {kind}");

        /// <summary>停止背景迴圈（等同程序結束）：進行中的執行標為中斷，迴圈結束後才回來。</summary>
        public async Task StopLoopAsync()
        {
            _stop.Cancel();
            await _loop;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop;
        }
    }

    [Fact]
    public async Task DueRun_CompletesOnce_WritesResult_AndNotifiesWithOpenTarget()
    {
        var provider = new AnswerProvider("整理好了\n第二行");
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync();

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);

        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);
        await h.WaitForNotificationAsync(NotificationKind.ScheduledRun, "『整理更新』完成了");
        Assert.Equal(ScheduledRunTrigger.Scheduled, run.Trigger);
        Assert.Equal(NineAm, run.PlannedAt);
        Assert.Equal(NineAm, run.StartedAt);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal("整理更新", run.ScheduleTitle);
        Assert.Equal(1, provider.Calls);

        var conversationId = Assert.IsType<string>(run.ConversationId);
        var messages = await h.Conversations.GetMessagesAsync(conversationId);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], messages.Select(m => m.Role));
        Assert.Equal(Prompt, Assert.IsType<TextBlock>(Assert.Single(messages[0].Content)).Text);

        var notification = Assert.Single(h.Sink.Shown, n => n.Kind == NotificationKind.ScheduledRun);
        Assert.Equal("整理好了", notification.Message);
        Assert.Equal(new OpenScheduledRun(run.Id), notification.Target);
        Assert.DoesNotContain(h.Sink.Shown, n => n.Kind == NotificationKind.Reminder);

        // 一次性排程跑完後項目本身完成
        Assert.Equal(ScheduledStatus.Completed, (await h.Items.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task TwoDueAtOnce_RunOneAfterAnother_ProviderNeverInFlightTwice()
    {
        var provider = new ScriptProvider(_ => Answer("完成"), before: _ => Task.Delay(20));
        await using var h = new RunHarness(provider);
        var first = await h.CreatePromptAsync(name: "甲");
        var second = await h.CreatePromptAsync(name: "乙");

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);

        await h.WaitForRunAsync(first.Id, ScheduledRunStatus.Completed);
        await h.WaitForRunAsync(second.Id, ScheduledRunStatus.Completed);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(1, provider.MaxInFlight);
    }

    [Fact]
    public async Task ScheduledRun_DoesNotBlockAnOrdinaryConversation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptProvider(_ => Answer("好"), before: request => LastUserText(request) == Prompt ? gate.Task : Task.CompletedTask);
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync();
        var ordinary = await h.Conversations.CreateAsync("一般對話");

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        await WaitUntilAsync(() => Task.FromResult(provider.Calls > 0), "排程的請求已送出");
        var run = (await h.Runs.ListForScheduleAsync(item.Id)).Single();
        Assert.Equal(ScheduledRunStatus.Running, run.Status);

        // 排程還卡在 provider 裡，一般對話照常送完
        var events = new List<ChatEvent>();
        await foreach (var chatEvent in h.Chat.SendAsync(ordinary.Id, "嗨"))
            events.Add(chatEvent);

        Assert.Contains(events, e => e is Completed);
        Assert.True(h.Runner.IsActive(run.ConversationId!));
        Assert.False(h.Runner.IsActive(ordinary.Id));

        gate.SetResult();
        await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);
    }

    [Fact]
    public async Task CatchUp_OneHourLate_RunsOnce_WithCatchUpTrigger()
    {
        var provider = new AnswerProvider();
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync();

        h.Clock.SetNow(T("2026-10-23T10:00+08:00"));
        await h.Reminders.OnStartupAsync(default);

        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);
        Assert.Equal(ScheduledRunTrigger.CatchUp, run.Trigger);
        Assert.Equal(NineAm, run.PlannedAt);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CatchUp_RecurringLate_RunsOnce_AndAdvancesToNextDay()
    {
        var provider = new AnswerProvider();
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync(trigger: new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(9, 0), null, null));

        h.Clock.SetNow(T("2026-10-23T10:30+08:00"));
        await h.Reminders.OnStartupAsync(default);

        await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);
        Assert.Single(await h.Runs.ListForScheduleAsync(item.Id));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(T("2026-10-24T09:00+08:00"), (await h.Items.GetAsync(item.Id))!.NextFireAt);
        Assert.Equal(ScheduledStatus.Pending, (await h.Items.GetAsync(item.Id))!.Status);
    }

    [Fact]
    public async Task ThreeHoursLate_RecordsMissed_WithoutConversation_AndNeverCallsProvider()
    {
        var provider = new AnswerProvider();
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync();

        h.Clock.SetNow(T("2026-10-23T12:00+08:00"));
        await h.Reminders.OnStartupAsync(default);

        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Missed);
        Assert.Null(run.ConversationId);
        Assert.Equal(NineAm, run.PlannedAt);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(ScheduledStatus.Missed, (await h.Items.GetAsync(item.Id))!.Status);
        var summary = Assert.Single(h.Sink.Shown);
        Assert.Equal(NotificationKind.MissedSummary, summary.Kind);
        Assert.Equal("你錯過了 1 個提醒或排程", summary.Title);
    }

    [Fact]
    public async Task Stop_InterruptsRun_AndToolResultSaysStopped()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FakeTool("slow")
        {
            Behavior = async (_, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return ToolResult.Ok("不會到這裡");
            },
        };
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "slow")));
        await using var h = new RunHarness(provider, tools: [tool]);
        var item = await h.CreatePromptAsync();

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        await started.Task;
        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Running);

        h.Runner.Stop(run.Id);

        await h.WaitForRunStatusByIdAsync(run.Id, ScheduledRunStatus.Interrupted);
        var conversationId = (await h.Runs.GetAsync(run.Id))!.ConversationId!;
        var messages = await h.Conversations.GetMessagesAsync(conversationId);
        var result = messages.SelectMany(m => m.Content.OfType<ToolResultBlock>()).Single();
        Assert.Equal("t1", result.ToolUseId);
        Assert.Equal("使用者已停止。", result.Content);
        Assert.False(h.Runner.IsActive(conversationId));
    }

    [Fact]
    public async Task StoppingTheLoop_InterruptsRunsInProgress()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new FakeTool("slow")
        {
            Behavior = async (_, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return ToolResult.Ok("不會到這裡");
            },
        };
        var provider = new SequenceChatProvider(SequenceChatProvider.ToolCalls(Use("t1", "slow")));
        var h = new RunHarness(provider, tools: [tool]);
        var item = await h.CreatePromptAsync();
        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        await started.Task;

        await h.StopLoopAsync();

        var run = Assert.Single(await h.Runs.ListForScheduleAsync(item.Id));
        Assert.Equal(ScheduledRunStatus.Interrupted, run.Status);
        Assert.NotNull(run.FinishedAt);
        await h.DisposeAsync();
    }

    [Fact]
    public async Task NoModel_RunFails_WithReason_AndNotifies()
    {
        var provider = new AnswerProvider();
        await using var h = new RunHarness(provider, catalog: new FakeModelCatalog(withModel: false));
        var item = await h.CreatePromptAsync();

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);

        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Failed);
        Assert.Equal("還沒有可用的模型，請先到設定加入連線與模型。", run.FailureText);
        await h.WaitForNotificationAsync(NotificationKind.ScheduledRun, "『整理更新』沒有完成");
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task UnexpectedError_RunFails_WithFixedSentence_AndNoInternalDetail()
    {
        var provider = new ScriptProvider(_ => throw new InvalidOperationException("秘密路徑 /Users/x/secret"));
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync();

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);

        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Failed);
        Assert.Equal("執行時發生錯誤，沒有完成。", run.FailureText);
        await h.WaitForNotificationAsync(NotificationKind.ScheduledRun, "『整理更新』沒有完成");
        Assert.DoesNotContain(h.Sink.Shown, n => n.Message.Contains("秘密"));
    }

    [Fact]
    public async Task MoreThanThirtyRuns_KeepsOnlyTheNewest()
    {
        var provider = new AnswerProvider();
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync();
        h.Clock.SetNow(NineAm);

        // 總共 1 + 預設保留數 次：最舊的那一次應該被清掉
        var first = await h.Runner.RunNowAsync(item.Id);
        await h.WaitForRunStatusByIdAsync(first!.Id, ScheduledRunStatus.Completed);
        var firstConversation = (await h.Runs.GetAsync(first.Id))!.ConversationId!;
        for (var i = 0; i < DefaultKeep; i++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(1));
            var next = await h.Runner.RunNowAsync(item.Id);
            await h.WaitForRunStatusByIdAsync(next!.Id, ScheduledRunStatus.Completed);
        }

        var kept = await h.Runs.ListForScheduleAsync(item.Id);
        Assert.Equal(DefaultKeep, kept.Count);
        Assert.DoesNotContain(kept, r => r.Id == first.Id);
        Assert.Null(await h.Runs.GetAsync(first.Id));
        Assert.Null(await h.Conversations.GetConversationAsync(firstConversation));
    }

    [Fact]
    public async Task ScopeItems_JoinTheConversation_WithoutSnapshotContent()
    {
        var provider = new AnswerProvider();
        await using var h = new RunHarness(provider);
        var scope = new[] { new ScheduleScopeItem(ContextKind.Folder, "/notes", "筆記") };
        var item = await h.CreatePromptAsync(scope: scope);

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);

        var contextItem = Assert.Single(await h.Contexts.GetAsync(run.ConversationId!));
        Assert.Equal("/notes", contextItem.Path);
        Assert.False(contextItem.IsPending);
        var request = Assert.Single(provider.Requests);
        var lastUser = request.Messages.Last(m => m.Role == ChatRole.User);
        Assert.DoesNotContain(lastUser.Content, b => b is ContextBlock);
    }

    [Fact]
    public async Task RunRequest_CarriesTheScheduledRunLayer_AfterTheModeLayer()
    {
        var provider = new AnswerProvider();
        var composer = new PromptComposer([new ModeFormatPromptLayer(), new ScheduledRunPromptLayer()]);
        await using var h = new RunHarness(provider, composer: composer);
        var item = await h.CreatePromptAsync(name: "晨間整理");

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);

        var suffix = Assert.Single(provider.Requests).Messages.Last(m => m.Role == ChatRole.User).TurnSuffix;
        Assert.NotNull(suffix);
        Assert.Contains("晨間整理", suffix);
        Assert.Contains("<lumi_scheduled_run>", suffix);
        Assert.True(suffix.IndexOf("<lumi_mode>", StringComparison.Ordinal) < suffix.IndexOf("<lumi_scheduled_run>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approval_PausesRun_ReleasesSlot_NextRunGoesFirst_ThenResumes()
    {
        var provider = new ScriptProvider(request =>
        {
            if (LastUserText(request) != Prompt)
                return Answer("乙完成");
            return request.Messages.Any(m => m.Role == ChatRole.Tool) ? Answer("甲完成") : ToolCalls(Use("w1", "write_it"));
        });
        var tool = new FakeTool("write_it", ToolPermission.Write);
        var approvals = new PausingApprovals();
        await using var h = new RunHarness(provider, tools: [tool], approvals: approvals);
        var first = await h.CreatePromptAsync(name: "甲", prompt: Prompt);
        var second = await h.CreatePromptAsync(name: "乙", prompt: "乙的工作");
        approvals.Host = h.Runner;
        approvals.Lookup = () => h.Runs.ListForScheduleAsync(first.Id).GetAwaiter().GetResult()
            .FirstOrDefault(r => r.ConversationId is not null)?.ConversationId;

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);

        // 甲停在確認，乙照常跑完：待確認的執行讓出位置
        await h.WaitForRunAsync(first.Id, ScheduledRunStatus.AwaitingApproval);
        await h.WaitForRunAsync(second.Id, ScheduledRunStatus.Completed);
        await h.WaitForNotificationAsync(NotificationKind.ScheduledRun, "『甲』需要你確認");
        var awaiting = h.Sink.Shown.Single(n => n.Title == "『甲』需要你確認");
        Assert.Equal("回聲", awaiting.Message);
        Assert.Equal(new OpenScheduledRun((await h.Runs.ListForScheduleAsync(first.Id)).Single().Id), awaiting.Target);

        approvals.Gate.SetResult(ApprovalDecision.AllowOnce);
        var done = await h.WaitForRunAsync(first.Id, ScheduledRunStatus.Completed);
        Assert.Null(done.FailureText);
    }

    [Fact]
    public async Task NewRunOfTheSchedule_InterruptsTheEarlierRunThatIsAwaitingApproval()
    {
        var provider = new ScriptProvider(request =>
            request.Messages.Any(m => m.Role == ChatRole.Tool) ? Answer("完成") : ToolCalls(Use("w1", "write_it")));
        var tool = new FakeTool("write_it", ToolPermission.Write);
        var approvals = new PausingApprovals();
        await using var h = new RunHarness(provider, tools: [tool], approvals: approvals);
        var item = await h.CreatePromptAsync();
        approvals.Host = h.Runner;
        approvals.Lookup = () => h.Runs.ListForScheduleAsync(item.Id).GetAwaiter().GetResult()
            .OrderBy(r => r.PlannedAt).LastOrDefault(r => r.ConversationId is not null)?.ConversationId;

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        var earlier = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.AwaitingApproval);

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Runner.RunNowAsync(item.Id);

        await h.WaitForRunStatusByIdAsync(earlier.Id, ScheduledRunStatus.Interrupted);
        var newer = (await h.Runs.ListForScheduleAsync(item.Id)).Single(r => r.Id != earlier.Id);
        await h.WaitForRunStatusByIdAsync(newer.Id, ScheduledRunStatus.AwaitingApproval);
    }

    [Fact]
    public async Task RunNow_ReturnsNull_ForMissingOrNonPromptItems()
    {
        await using var h = new RunHarness(new AnswerProvider());
        var reminder = await h.Reminders.CreateReminderAsync(ReminderTestData.OneTime("2026-10-23T09:00+08:00"), default);

        Assert.Null(await h.Runner.RunNowAsync("missing"));
        Assert.Null(await h.Runner.RunNowAsync(reminder.Id));
    }

    [Fact]
    public async Task RunNow_RunsManually_AndDoesNotTouchNextFireAt()
    {
        var provider = new AnswerProvider();
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync(trigger: new RecurrenceTrigger(RecurrenceKind.Daily, new TimeOnly(9, 0), null, null));
        h.Clock.SetNow(T("2026-10-23T08:30+08:00"));

        var queued = await h.Runner.RunNowAsync(item.Id);

        var run = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);
        Assert.Equal(ScheduledRunTrigger.Manual, run.Trigger);
        Assert.Equal(queued!.Id, run.Id);
        Assert.Equal(T("2026-10-23T08:30+08:00"), run.PlannedAt);
        Assert.Equal(T("2026-10-23T09:00+08:00"), (await h.Items.GetAsync(item.Id))!.NextFireAt);
    }

    [Fact]
    public async Task FindByConversation_ReturnsGrantFromTheSchedule_WhileRunning()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptProvider(_ => Answer("好"), before: _ => gate.Task);
        await using var h = new RunHarness(provider);
        var grant = new ScheduleGrant(Write: true, Network: false, Destructive: false);
        var item = await h.CreatePromptAsync(grant: grant);

        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        var running = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Running);
        await WaitUntilAsync(async () => (await h.Runs.GetAsync(running.Id))!.ConversationId is not null, "對話已建立");

        var active = h.Runner.FindByConversation((await h.Runs.GetAsync(running.Id))!.ConversationId!);
        Assert.NotNull(active);
        Assert.Equal(running.Id, active!.RunId);
        Assert.Equal(item.Id, active.ScheduleId);
        Assert.Equal(grant, active.Grant);
        Assert.Null(h.Runner.FindByConversation("某個一般對話"));

        gate.SetResult();
        await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);
    }

    [Fact]
    public async Task DeleteRun_WhileActive_ReturnsFalse_AndAfterwardsRemovesRunAndConversation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptProvider(_ => Answer("好"), before: _ => gate.Task);
        await using var h = new RunHarness(provider);
        var item = await h.CreatePromptAsync();
        h.Clock.SetNow(NineAm);
        await h.Reminders.ProcessDueAsync(default);
        var running = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Running);

        Assert.False(await h.Runner.DeleteRunAsync(running.Id));
        Assert.NotNull(await h.Runs.GetAsync(running.Id));

        gate.SetResult();
        var done = await h.WaitForRunAsync(item.Id, ScheduledRunStatus.Completed);
        Assert.True(await h.Runner.DeleteRunAsync(done.Id));
        Assert.Null(await h.Runs.GetAsync(done.Id));
        Assert.Null(await h.Conversations.GetConversationAsync(done.ConversationId!));
        Assert.False(await h.Runner.DeleteRunAsync(done.Id));
    }

    [Fact]
    public async Task DeleteRunsForSchedule_RemovesAllFinishedRuns()
    {
        await using var h = new RunHarness(new AnswerProvider());
        var item = await h.CreatePromptAsync();
        h.Clock.SetNow(NineAm);
        var first = await h.Runner.RunNowAsync(item.Id);
        await h.WaitForRunStatusByIdAsync(first!.Id, ScheduledRunStatus.Completed);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await h.Runner.RunNowAsync(item.Id);
        await h.WaitForRunStatusByIdAsync(second!.Id, ScheduledRunStatus.Completed);

        var deleted = await h.Runner.DeleteRunsForScheduleAsync(item.Id);

        Assert.Equal(2, deleted);
        Assert.Empty(await h.Runs.ListForScheduleAsync(item.Id));
    }

    [Fact]
    public async Task MarkRead_SetsReadAt()
    {
        await using var h = new RunHarness(new AnswerProvider());
        var item = await h.CreatePromptAsync();
        h.Clock.SetNow(NineAm);
        var run = await h.Runner.RunNowAsync(item.Id);
        await h.WaitForRunStatusByIdAsync(run!.Id, ScheduledRunStatus.Completed);
        Assert.True((await h.Runs.GetAsync(run.Id))!.IsUnread);

        await h.Runner.MarkReadAsync(run.Id);

        Assert.False((await h.Runs.GetAsync(run.Id))!.IsUnread);
    }

    [Fact]
    public async Task RecoverOnStartup_MarksActiveRunsInterrupted()
    {
        await using var h = new RunHarness(new AnswerProvider());
        var item = await h.CreatePromptAsync();
        await h.Runs.AddAsync(new ScheduledRun(ScheduledRun.NewId(), item.Id, item.Title, "c1", NineAm, NineAm, null,
            ScheduledRunTrigger.Scheduled, ScheduledRunStatus.Running, null, null));
        await h.Runs.AddAsync(new ScheduledRun(ScheduledRun.NewId(), item.Id, item.Title, "c2", NineAm, NineAm, null,
            ScheduledRunTrigger.Scheduled, ScheduledRunStatus.AwaitingApproval, null, null));

        await h.Runner.RecoverOnStartupAsync(default);

        Assert.All(await h.Runs.ListForScheduleAsync(item.Id), r => Assert.Equal(ScheduledRunStatus.Interrupted, r.Status));
    }

    [Fact]
    public async Task AddGrant_WritesThePermissionIntoTheSchedule()
    {
        await using var h = new RunHarness(new AnswerProvider());
        var item = await h.CreatePromptAsync(grant: ScheduleGrant.None);

        await h.Runner.AddGrantAsync(item.Id, ToolPermission.Write, default);

        var action = Assert.IsType<RunPromptAction>((await h.Items.GetAsync(item.Id))!.Action);
        Assert.Equal(new ScheduleGrant(Write: true, Network: false, Destructive: false), action.Grant);
    }

    [Fact]
    public async Task RecordMissed_IsRecordedWithoutConversation_AndNotNotified()
    {
        await using var h = new RunHarness(new AnswerProvider());
        var item = await h.CreatePromptAsync();

        await h.Runner.RecordMissedAsync(item, NineAm, default);

        var run = Assert.Single(await h.Runs.ListForScheduleAsync(item.Id));
        Assert.Equal(ScheduledRunStatus.Missed, run.Status);
        Assert.Null(run.ConversationId);
        Assert.Empty(h.Sink.Shown);
    }
}
