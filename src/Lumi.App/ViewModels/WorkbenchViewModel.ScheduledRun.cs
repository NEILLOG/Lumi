using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;

namespace Lumi.App.ViewModels;

/// <summary>
/// 排程紀錄檢視（M24 包 24F）：工作台內容區換成一則排程執行的紀錄，輸入區換成說明列，「接著聊」開一則新對話。
/// 目前對話的 <see cref="ConversationTimeline"/> 照常在背後更新，關掉紀錄檢視時不需要重讀。
/// </summary>
public partial class WorkbenchViewModel : IScheduledRunViewer
{
    /// <summary>正在看的排程紀錄；不是 null 時內容區顯示紀錄、輸入區換成說明列（ui-spec 2.2 的橫幅也在這裡）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewingRun), nameof(ShownMessages), nameof(ShowWelcome))]
    private ScheduledRunViewModel? _viewingRun;

    public bool IsViewingRun => ViewingRun is not null;

    /// <summary>訊息區顯示的內容：看排程紀錄時是紀錄的訊息，否則是目前的對話。</summary>
    public IEnumerable<MessageViewModel> ShownMessages => ViewingRun is { } run ? run.Messages : Messages;

    /// <summary>
    /// 打開一筆排程紀錄（歷史抽屜、通知、引用卡）。紀錄已被清掉就什麼都不做。小精靈回應中不開（直接回來）；
    /// 小精靈沒在回應時先切到工作台模式再顯示。打開即標已讀。
    /// </summary>
    public async Task OpenScheduledRunAsync(string runId)
    {
        if (_scheduledRuns is null || _runStore is null)
            return;
        var run = await _runStore.GetAsync(runId);
        if (run is null)
            return;

        if (_session.ActiveMode == ChatMode.Sprite)
        {
            if (_session.IsResponding)
                return;   // 小精靈回應中：不開工作台
            _modes?.OpenWorkbench();   // 小精靈沒在回應：切到工作台模式（小精靈隱藏）再顯示
        }

        await _scheduledRuns.MarkReadAsync(runId);
        var view = new ScheduledRunViewModel(run, _scheduledRuns, _store, _dispatcher, _approvals, _clipboard, _time, _runLogger, _confirm, _toolCatalog);
        await view.LoadAsync();
        view.Subscribe();   // 先讀存檔的內容，再接收之後的事件
        SetViewingRun(view);
    }

    /// <summary>「回到目前對話」。</summary>
    [RelayCommand]
    private void BackToChat() => CloseRunView();

    /// <summary>「停止這次執行」（執行中或待確認）。</summary>
    [RelayCommand]
    private void StopViewingRun()
    {
        if (ViewingRun is { } run)
            _scheduledRuns?.Stop(run.RunId);
    }

    /// <summary>「現在執行」（錯過、中斷、失敗）：立即執行同一個排程，打開新的這次。</summary>
    [RelayCommand]
    private async Task RunViewAgainAsync()
    {
        if (ViewingRun is not { } run || _scheduledRuns is null)
            return;
        if (await _scheduledRuns.RunNowAsync(run.ScheduleId) is { } started)
            await OpenScheduledRunAsync(started.Id);
    }

    /// <summary>「接著聊」：停止目前的回應（<see cref="IChatSession.NewConversationAsync"/> 處理），開一則新對話並加入引用卡，關掉紀錄檢視。</summary>
    [RelayCommand]
    private async Task ContinueFromRunAsync()
    {
        if (ViewingRun is not { CanContinue: true } run)
            return;
        var title = string.Format(CultureInfo.CurrentCulture, Strings.Schedule_ContinueChatTitleFormat, run.Title);
        var conversation = await NavigateAsync(() => _session.NewConversationAsync(title));
        await LoadAsync(conversation);   // 切到新對話：紀錄檢視關掉
        await Context.AddScheduledRunAsync(run.RunId, run.Title);
        await History.RefreshAsync(CurrentConversationId);
    }

    /// <summary>使用者氣泡上引用卡的「查看原紀錄」。</summary>
    [RelayCommand]
    private Task OpenQuotedRunAsync(MessageChipViewModel? chip) =>
        chip?.RunId is { } runId ? OpenScheduledRunAsync(runId) : Task.CompletedTask;

    private void SetViewingRun(ScheduledRunViewModel? view)
    {
        var previous = ViewingRun;
        ViewingRun = view;
        previous?.Dispose();
    }

    /// <summary>關掉紀錄檢視，回到目前的對話（它的訊息一直在背後更新）。</summary>
    private void CloseRunView()
    {
        if (ViewingRun is not null)
            SetViewingRun(null);
    }
}
