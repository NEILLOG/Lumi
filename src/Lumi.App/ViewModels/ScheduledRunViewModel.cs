using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>
/// 排程紀錄檢視（M24 4.7.2）的內容：一次排程執行的唯讀訊息列表，工作台內容區換成它顯示。
/// 進行中就訂閱 <see cref="IScheduledRunService.EventReceived"/> 即時追加；狀態改變（<see cref="IScheduledRunService.RunUpdated"/>）時更新橫幅與說明列，
/// 結束後重讀存檔的對話（和存檔一致）。工具卡片是 <c>IsScheduledRun</c> 的卡，確認直接走 <see cref="IToolApprovalResponder"/>。
/// 事件在背景執行緒發出，這裡一律用 <see cref="IUiDispatcher"/> 切回 UI 執行緒。
/// </summary>
public partial class ScheduledRunViewModel : ObservableObject, IDisposable
{
    private readonly IScheduledRunService _service;
    private readonly IConversationStore _store;
    private readonly IUiDispatcher _dispatcher;
    private readonly IToolApprovalResponder _approvals;
    private readonly IClipboardService _clipboard;
    private readonly TimeProvider _time;
    private readonly ILogger<ScheduledRunViewModel> _logger;
    private readonly Dictionary<string, ToolCardViewModel> _cards = new(StringComparer.Ordinal);
    private MessageViewModel? _bubble;
    private bool _subscribed;
    private bool _disposed;

    private readonly IConfirmService? _confirm;
    private readonly IToolCatalog? _toolCatalog;

    public ScheduledRunViewModel(
        ScheduledRun run, IScheduledRunService service, IConversationStore store, IUiDispatcher dispatcher,
        IToolApprovalResponder approvals, IClipboardService clipboard, TimeProvider time, ILogger<ScheduledRunViewModel> logger,
        IConfirmService? confirm = null, IToolCatalog? toolCatalog = null)
    {
        _confirm = confirm;
        _toolCatalog = toolCatalog;
        _run = run;
        _service = service;
        _store = store;
        _dispatcher = dispatcher;
        _approvals = approvals;
        _clipboard = clipboard;
        _time = time;
        _logger = logger;
    }

    /// <summary>目前顯示的紀錄（狀態會隨 <see cref="IScheduledRunService.RunUpdated"/> 更新）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunId), nameof(ScheduleId), nameof(ConversationId), nameof(Title), nameof(BannerText),
        nameof(StatusNote), nameof(HasStatusNote), nameof(CanStop), nameof(CanContinue), nameof(CanRunAgain), nameof(ShowBarNote))]
    private ScheduledRun _run;

    /// <summary>紀錄的訊息（唯讀）：那次執行的 prompt、助理回答、工具段落；進行中的會即時追加。</summary>
    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public string RunId => Run.Id;

    public string ScheduleId => Run.ScheduleId;

    /// <summary>紀錄的對話；錯過的沒有對話，是 null。</summary>
    public string? ConversationId => Run.ConversationId;

    /// <summary>排程名稱（快照）。</summary>
    public string Title => Run.ScheduleTitle;

    /// <summary>執行時間（開始時間，沒有開始就是排定時間）。</summary>
    public string TimeText => ScheduledRunContext.FormatTime(Run.StartedAt ?? Run.PlannedAt);

    /// <summary>橫幅文字：名稱、執行時間；補跑、立即執行另外標註。</summary>
    public string BannerText
    {
        get
        {
            var time = TimeText;
            var trigger = Run.Trigger switch
            {
                ScheduledRunTrigger.CatchUp => Strings.Schedule_Trigger_CatchUp,
                ScheduledRunTrigger.Manual => Strings.Schedule_Trigger_Manual,
                _ => null,
            };
            if (trigger is not null)
                time = $"{time} · {trigger}";
            return string.Format(CultureInfo.CurrentCulture, Strings.Schedule_ViewBannerFormat, Title, time);
        }
    }

    /// <summary>狀態說明：錯過、中斷、失敗時各有一句（失敗的原因來自 <see cref="ScheduledRun.FailureText"/>）。</summary>
    public string? StatusNote => Run.Status switch
    {
        ScheduledRunStatus.Missed => Strings.Schedule_MissedNote,
        ScheduledRunStatus.Interrupted => Strings.Schedule_InterruptedNote,
        ScheduledRunStatus.Failed => string.Format(CultureInfo.CurrentCulture, Strings.Schedule_FailedNoteFormat, Run.FailureText ?? ""),
        _ => null,
    };

    public bool HasStatusNote => StatusNote is not null;

    /// <summary>執行中或待確認：橫幅上有「停止這次執行」。</summary>
    public bool CanStop => Run.IsActive;

    /// <summary>有對話才能接著聊（錯過沒有對話）。</summary>
    public bool CanContinue => ConversationId is not null;

    /// <summary>說明列的「這是排程紀錄，不能在這裡輸入」那句：只有能接著聊（有對話）時才說明下一步。</summary>
    public bool ShowBarNote => CanContinue;

    /// <summary>錯過、中斷、失敗：可以「現在執行」。</summary>
    public bool CanRunAgain => Run.Status is ScheduledRunStatus.Missed or ScheduledRunStatus.Interrupted or ScheduledRunStatus.Failed;

    /// <summary>這次的排程授權是否排除在「以後都允許」之外（整合時給工具卡片的 <c>ScheduleExcluded</c> 用）。</summary>
    public static bool IsExcluded(ToolDescriptor descriptor, ToolApprovalRequest request) => ScheduledRunApprovalPolicy.IsExcluded(descriptor, request);

    // ---- 載入與訂閱 ----

    /// <summary>從存檔的對話重建訊息（UI 執行緒）。進行中的執行先讀存檔的部分，之後的內容由事件追加。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ChatMessage> history = ConversationId is { } id
            ? await _store.GetMessagesAsync(id, ct)
            : Array.Empty<ChatMessage>();

        _cards.Clear();
        _bubble = null;
        // 進行中（含待確認）的執行：還沒有結果是正常的，不要標成「沒有結果」
        var items = ConversationTimeline.Build(
            history, use => CreateCard(use.Id, use.Name, use.Name, null, null, use.InputJson), markUnfinished: !Run.IsActive);
        Messages.Clear();
        foreach (var item in items)
        {
            item.ShowScheduleBadge = item.IsUser;   // 紀錄裡的使用者訊息就是排程送出的 prompt
            Messages.Add(item);
        }

        // 待確認：訂閱之前就已經送出的確認請求，補回確認按鈕
        if (Run.Status == ScheduledRunStatus.AwaitingApproval
            && _service.GetPendingApproval(Run.Id) is { } pending
            && _cards.TryGetValue(pending.CallId, out var pendingCard))
        {
            MarkExclusion(pendingCard, pending);
            pendingCard.SetAwaiting(pending);
        }
        UpdateFlags();
    }

    /// <summary>「以後都允許」不適用的工具（記憶、上傳、管理排程、沒出現過的網站…）：卡片隱藏那顆按鈕（M24）。</summary>
    private void MarkExclusion(ToolCardViewModel card, ToolApprovalRequest request)
    {
        var descriptor = _toolCatalog?.Find(request.ToolName)?.Descriptor;
        card.ScheduleExcluded = descriptor is null || IsExcluded(descriptor, request);
    }

    /// <summary>開始接收事件與狀態改變（打開之後才訂閱，結束或換掉時 <see cref="Dispose"/> 取消）。</summary>
    public void Subscribe()
    {
        if (_subscribed || _disposed)
            return;
        _subscribed = true;
        _service.EventReceived += OnEventReceived;
        _service.RunUpdated += OnRunUpdated;
        _approvals.Resolved += OnApprovalResolved;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.EventReceived -= OnEventReceived;
        _service.RunUpdated -= OnRunUpdated;
        _approvals.Resolved -= OnApprovalResolved;
    }

    private void OnEventReceived(object? sender, ScheduledRunEventArgs args)
    {
        if (args.RunId != RunId)
            return;
        _dispatcher.Post(() => Apply(args.Event));
    }

    private void OnRunUpdated(object? sender, ScheduledRun run)
    {
        if (run.Id != RunId)
            return;
        _dispatcher.Post(() => ApplyRunState(run));
    }

    private void ApplyRunState(ScheduledRun updated)
    {
        if (_disposed)
            return;
        var wasActive = Run.IsActive;
        Run = updated;
        // 剛結束：重讀存檔的對話，畫面和存檔一致（串流中途的文字、已停止的回答都以存檔為準）
        if (wasActive && !updated.IsActive)
            _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "重讀排程紀錄 {RunId} 的對話時失敗", RunId);
        }
    }

    private void OnApprovalResolved(object? sender, ApprovalResolved resolved)
    {
        if (_cards.TryGetValue(resolved.CallId, out var card))
            card.ApplyResolved(resolved.Decision);
    }

    // ---- 即時事件（和工作台的回應流程同一套規則，只是不碰工作台的狀態） ----

    private void Apply(ChatEvent chatEvent)
    {
        if (_disposed)
            return;
        var now = _time.GetUtcNow();
        switch (chatEvent)
        {
            case TextDelta delta:
                (_bubble ??= StartBubble()).AppendText(delta.Text, now);
                break;
            case ThinkingDelta delta:
                (_bubble ??= StartBubble()).AppendThinking(delta.Text, now);
                break;
            case Completed completed when completed.Reason == StopReason.ToolUse:
                // 中間步驟：有文字就定稿成一個氣泡，只有思考或只有工具呼叫不顯示
                if (completed.Message.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0))
                    (_bubble ??= StartBubble()).Complete(completed.Message, now);
                else
                    DropBubble();
                _bubble = null;
                break;
            case Completed completed:
                (_bubble ??= StartBubble()).Complete(completed.Message, now);
                _bubble = null;
                break;
            case ToolCallStarted started when _cards.ContainsKey(started.CallId):
                break;   // 打開時存檔裡已經有這張卡（呼叫在打開前一刻才開始）
            case ToolCallStarted started:
            {
                if (_bubble is { HasText: false, HasThinking: false })
                    DropBubble();
                // 最後是只放附件的訊息、前面是工具段落：接在同一段（和工作台相同，ui-spec 3.1）
                if (ReplyAttachmentLayout.OpenRunForNewCall(Messages) is not { } run)
                {
                    run = new ToolRunViewModel();
                    Messages.Add(run);
                }
                run.Cards.Add(CreateCard(started.CallId, started.ToolName, started.DisplayName, started.Permission, started.Target, started.ArgumentsJson));
                break;
            }
            case ApprovalRequired approval:
                if (_cards.TryGetValue(approval.Request.CallId, out var waiting))
                {
                    MarkExclusion(waiting, approval.Request);
                    waiting.SetAwaiting(approval.Request);
                }
                break;
            case ToolCallProgress progress:
                if (_cards.TryGetValue(progress.CallId, out var progressCard))
                    progressCard.SetProgress(progress.Text);
                break;
            case ToolCallCompleted finished:
                if (_cards.TryGetValue(finished.CallId, out var finishedCard))
                    finishedCard.Complete(finished.Result);
                _bubble ??= StartBubble();   // 工具跑完，等模型根據結果回應
                break;
            default:
                return;
        }
        UpdateFlags();
    }

    private ToolCardViewModel CreateCard(string callId, string toolName, string displayName, ToolPermission? permission, string? target, string argumentsJson)
    {
        var card = new ToolCardViewModel(callId, toolName, displayName, permission, target, argumentsJson, _approvals, _clipboard, _time, _confirm)
        {
            IsScheduledRun = true,
        };
        // 附件換掉時（完成）重新分配；卡片與這個紀錄檢視同生命週期
        card.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolCardViewModel.ReplyAttachments) && !_disposed)
                UpdateFlags();
        };
        _cards[callId] = card;
        return card;
    }

    private MessageViewModel StartBubble()
    {
        var created = MessageViewModel.StreamingAssistant(_time.GetUtcNow());
        Messages.Add(created);
        return created;
    }

    private void DropBubble()
    {
        if (_bubble is not null)
            Messages.Remove(_bubble);
        _bubble = null;
    }

    /// <summary>附件歸屬、停止狀態，以及每則訊息是不是後面緊接著工具段落（和工作台的 RefreshMessageState 相同）。</summary>
    private void UpdateFlags()
    {
        ReplyAttachmentLayout.Apply(Messages);
        ReplyAttachmentLayout.MarkStopped(Messages);
        for (var i = 0; i < Messages.Count; i++)
            Messages[i].IsFollowedByTools = i + 1 < Messages.Count && Messages[i + 1] is ToolRunViewModel;
    }
}
