using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台的支線（M36 包 36F）：開支線、問這段、打開支線、帶回、放棄、回到上一層；路徑列、上一層被鎖住、輸入框與工具列的狀態。
/// 支線的資料與規則在 <see cref="IBranchService"/>，這裡只負責接到畫面。沒有支線服務（或是 <see cref="NullBranchService"/>）時整個功能不顯示。
/// </summary>
public partial class WorkbenchViewModel : IBranchBarHost
{
    /// <summary>支線服務；null＝沒有支線功能（<see cref="NullBranchService"/> 也當成 null）。</summary>
    private IBranchService? _branches;
    private IBranchStore? _branchStore;
    private IBranchMergePrompt? _mergePrompt;

    /// <summary>目前對話在支線結構裡的狀態（<see cref="RefreshBranchStateAsync"/> 更新）。只有它的路徑最後一節是目前對話時才算數（<see cref="CurrentBranchState"/>）。</summary>
    private BranchState? _branchState;

    /// <summary>上一層被鎖住時，進行中的那條（直接下一層）的標題。</summary>
    private string? _lockedChildTitle;

    /// <summary>工作台頂端、訊息列表上方的支線路徑列（36F）。</summary>
    public BranchBarViewModel BranchBar { get; private set; } = null!;

    /// <summary>新建支線之後發，View 播進入動畫（路徑列；色條與出處卡片在 36G）。</summary>
    public event EventHandler? BranchEntered;

    /// <summary>要求把焦點移到輸入框並全選（「問這段」預先填入的問題）。由 ChatComposer 處理。</summary>
    public event EventHandler? SelectInputRequested;

    private void InitializeBranch(IBranchService? branches, IBranchStore? store, IBranchMergePrompt? mergePrompt)
    {
        BranchBar = new BranchBarViewModel(this);
        if (branches is null or NullBranchService)
            return;   // 沒有支線功能：整組不顯示
        _branches = branches;
        _branchStore = store;
        _mergePrompt = mergePrompt;
        // 支線的變動可能來自別處（例如帶回對話框）：切回 UI 執行緒再查
        branches.Changed += (_, args) => _dispatcher.Post(() => _ = OnBranchChangedAsync(args));
    }

    /// <summary>有支線功能（服務不是 null）。</summary>
    private bool BranchAvailable => _branches is not null;

    /// <summary>目前對話的支線狀態；狀態不是目前對話的就是 null。</summary>
    private BranchState? CurrentBranchState => _branchState is { } state && CurrentConversationId is { } id && state.Path[^1].ConversationId == id ? state : null;

    /// <summary>目前開著的是支線（進行中或已結束）。訊息區的色條綁這個。</summary>
    public bool IsInBranch => CurrentBranchState is { IsBranch: true };

    /// <summary>目前開著的支線已經帶回或放棄（唯讀）。</summary>
    public bool IsBranchFinished => CurrentBranchState is { IsFinished: true };

    /// <summary>目前對話（上一層）有進行中的支線，所以被鎖住：送出、重試、重新產生、整理都停用（E13）。</summary>
    public bool IsBranchLocked => CurrentBranchState is { IsLocked: true };

    /// <summary>研究本、用量費用算在主線那則對話（D8、E5）：支線裡就是路徑的第一節。</summary>
    private string? RootConversationId => CurrentBranchState?.Root.ConversationId ?? CurrentConversationId;

    /// <summary>輸入框能不能打字：回應中（小精靈查看）、上一層被鎖住、支線已經結束都不能。</summary>
    public bool InputEnabled => !IsViewOnly && !IsBranchLocked && !IsBranchFinished;

    /// <summary>輸入框的佔位字：支線裡換成支線的字，鎖住與結束時說明為什麼不能打字。</summary>
    public string ComposerPlaceholder => IsBranchLocked ? Strings.Branch_InputLocked
        : IsBranchFinished ? Strings.Branch_InputFinished
        : IsInBranch ? Strings.Branch_InputPlaceholder
        : Strings.Workbench_InputPlaceholder;

    /// <summary>
    /// 從一則回答開支線（動作列）。開好後直接切到支線（同 <see cref="OpenConversationAsync"/>），發 <see cref="BranchEntered"/>。
    /// 不能開時（服務丟例外）用提示列說明。
    /// </summary>
    [RelayCommand]
    private async Task StartBranchAsync(MessageViewModel? message)
    {
        if (_branches is null || message is null || !message.CanBranch || message.MessageId is not { } forkId)
            return;
        if (CurrentConversationId is not { } parentId)
            return;
        try
        {
            var conversation = await _branches.StartAsync(parentId, forkId, null);
            await OpenConversationAsync(conversation);
            OnBranchEntered();
            FocusInputRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException ex)
        {
            Notice = ex.Message;
        }
    }

    /// <summary>
    /// 「問這段」：以選取的文字開支線，輸入框預先填「「…」是什麼意思？」並全選。
    /// </summary>
    [RelayCommand]
    private async Task AskAboutSelectionAsync(BranchQuoteRequest? request)
    {
        if (_branches is null || request is null)
            return;
        var message = request.Message;
        if (!message.CanBranch || message.MessageId is not { } forkId)
            return;
        if (CurrentConversationId is not { } parentId)
            return;
        try
        {
            var conversation = await _branches.StartAsync(parentId, forkId, request.Quote);
            await OpenConversationAsync(conversation);
            InputText = string.Format(CultureInfo.CurrentCulture, Strings.Branch_QuestionDraftFormat, ShortenForDraft(request.Quote));
            OnBranchEntered();
            SelectInputRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException ex)
        {
            Notice = ex.Message;
        }
    }

    private static string ShortenForDraft(string quote) =>
        quote.Length > BranchLimits.QuoteDraftChars ? quote[..BranchLimits.QuoteDraftChars] + "…" : quote;

    /// <summary>打開一條支線（摘要卡的「看完整支線」、歷史抽屜）。</summary>
    [RelayCommand]
    private Task OpenBranchAsync(string? conversationId) =>
        string.IsNullOrEmpty(conversationId) ? Task.CompletedTask : OpenConversationByIdAsync(conversationId);

    /// <summary>依 id 打開一則對話（路徑列的各節、摘要卡、歷史抽屜）；找不到就什麼都不做。</summary>
    public async Task OpenConversationByIdAsync(string conversationId)
    {
        if (_branches is null)
            return;
        var conversation = await _store.GetConversationAsync(conversationId);
        if (conversation is not null)
            await OpenConversationAsync(conversation);
    }

    private void OnBranchEntered() => BranchEntered?.Invoke(this, EventArgs.Empty);

    // ---- 帶回、放棄、回到上一層（路徑列與 IBranchBarHost）----

    /// <summary>帶回：一層是一步；全部帶回（第二層）是兩步，由深到淺（C7）。摘要由帶回對話框處理（<see cref="IBranchMergePrompt"/>）。</summary>
    public async Task MergeBranchAsync(bool all)
    {
        if (_mergePrompt is null || CurrentBranchState is not { IsBranch: true, IsFinished: false } state)
            return;

        var steps = new List<BranchMergeStep>();
        for (var i = state.Depth; i >= (all ? 1 : state.Depth); i--)
            steps.Add(await MergeStepAsync(state.Path[i], state.Path[i - 1], targetIsMain: i == 1));

        var request = new BranchMergeRequest(steps, ModelSwitcher.HasCurrent ? ModelSwitcher.CapsuleText : null, state.Root.Title);
        var result = await _mergePrompt.PromptAsync(request);
        if (result.OpenTo is { } target)
            await OpenConversationAsync(target);
    }

    private async Task<BranchMergeStep> MergeStepAsync(BranchPathNode branch, BranchPathNode target, bool targetIsMain)
    {
        var messages = await _store.GetMessagesAsync(branch.ConversationId);
        return new BranchMergeStep(branch.ConversationId, branch.Title, target.Title, targetIsMain, messages.Count);
    }

    /// <summary>放棄：有訊息時先確認（紀錄會保留，不是 destructive）；回到上一層。</summary>
    public async Task AbandonBranchAsync()
    {
        if (_branches is null || CurrentBranchState is not { IsBranch: true, IsFinished: false })
            return;
        if (CurrentConversationId is not { } id)
            return;
        if (_confirm is not null && (await _store.GetMessagesAsync(id)).Count > 0
            && !await _confirm.ConfirmAsync(Strings.Branch_AbandonConfirm, Strings.Branch_Abandon))
            return;
        try
        {
            var parent = await _branches.AbandonAsync(id);
            await OpenConversationAsync(parent);
        }
        catch (InvalidOperationException ex)
        {
            Notice = ex.Message;
        }
    }

    /// <summary>已結束的支線：回到上一層（主線或上一層的支線）。</summary>
    public Task BackFromBranchAsync() =>
        CurrentBranchState is { IsBranch: true, IsFinished: true } state
            ? OpenConversationByIdAsync(state.Path[^2].ConversationId)
            : Task.CompletedTask;

    /// <summary>上一層被鎖住：回到最深的那條進行中支線（<see cref="IBranchService.FindActiveLeafAsync"/>）。</summary>
    public async Task ReturnToBranchAsync()
    {
        if (_branches is null || CurrentConversationId is not { } id)
            return;
        if (await _branches.FindActiveLeafAsync(id) is { } leaf)
            await OpenConversationAsync(leaf);
    }

    /// <summary>D14：重開 Lumi 時工作台開的是最近一則主線；有進行中的支線就接著開它。</summary>
    private async Task OpenActiveBranchAsync(string conversationId)
    {
        if (_branches is null)
            return;
        try
        {
            if (await _branches.FindActiveLeafAsync(conversationId) is { } leaf && leaf.Id != CurrentConversationId)
                await OpenConversationAsync(leaf);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "開啟進行中的支線時失敗");
        }
    }

    // ---- 狀態：讀取、通知、訊息列的旗標、路徑列 ----

    /// <summary>
    /// 支線服務的變動：目前對話、它的上一層或主線有關時重新讀狀態，訊息列重建（出處卡片與摘要卡在這裡算），歷史清單跟著更新。
    /// </summary>
    private async Task OnBranchChangedAsync(BranchChangedArgs args)
    {
        var id = CurrentConversationId;
        if (id is null)
            return;
        if (args.ConversationId != id && args.ParentId != id && args.RootId != RootConversationId)
            return;
        try
        {
            await RefreshBranchStateAsync();
            if (!IsResponding && id == CurrentConversationId)
                await ReloadMessagesAsync();
            await History.RefreshAsync(CurrentConversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "支線狀態變了，重新讀取時失敗");
        }
    }

    /// <summary>查目前對話的支線狀態（與被鎖住時的下一層標題），再更新按鈕與路徑列。切換對話、支線變動時呼叫。</summary>
    /// <summary>
    /// 畫面上有「還沒交出」的摘要卡時，這一輪送出的使用者訊息已經把它交出（ConversationService 存訊息時就標記，不論回應成敗）：
    /// 重讀支線狀態與訊息，卡片才會移到那則使用者訊息上方、說明文字消失（M36-09）。
    /// </summary>
    private void DeferDeliveredBranchCards(RunFinishedArgs args)
    {
        if (_branches is null || CurrentConversationId is null || !Messages.Any(m => m is BranchSummaryCardViewModel { IsPending: true }))
            return;
        args.Defer(ReloadAfterDeliveryAsync());

        async Task ReloadAfterDeliveryAsync()
        {
            await RefreshBranchStateAsync();
            await ReloadMessagesAsync();
        }
    }

    private async Task RefreshBranchStateAsync()
    {
        var id = CurrentConversationId;
        BranchState? state = null;
        string? lockedTitle = null;
        if (_branches is not null && id is not null)
        {
            try
            {
                state = await _branches.GetStateAsync(id);
                if (state.ActiveChild is { } child)
                    lockedTitle = (await _store.GetConversationAsync(child.ConversationId))?.Title ?? BranchLimits.DefaultTitle;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "讀取支線狀態失敗");
                state = null;
                lockedTitle = null;
            }
        }
        if (id != CurrentConversationId)
            return;   // 查詢途中換了對話：那邊的載入會再查一次
        _branchState = state;
        _lockedChildTitle = lockedTitle;
        NotifyBranchState();
    }

    private void NotifyBranchState()
    {
        OnPropertyChanged(nameof(IsInBranch));
        OnPropertyChanged(nameof(IsBranchFinished));
        OnPropertyChanged(nameof(IsBranchLocked));
        OnPropertyChanged(nameof(InputEnabled));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(CanCompactAndContinue));
        OnPropertyChanged(nameof(CompactTip));
        OnPropertyChanged(nameof(ContextNoteButtonEnabled));
        OnPropertyChanged(nameof(ContextNoteButtonTip));
        SendCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RegenerateCommand.NotifyCanExecuteChanged();
        CompactAndContinueCommand.NotifyCanExecuteChanged();
        OpenContextNoteCommand.NotifyCanExecuteChanged();
        RefreshMessageState();   // 開支線的按鈕與路徑列在裡面重算
    }

    /// <summary>
    /// 每則助理回答的「開支線」：顯示條件與停用原因（4.8）。回應開始與結束、支線狀態變動時重算（由 <see cref="RefreshMessageState"/> 呼叫）。
    /// </summary>
    private void ApplyBranchActions()
    {
        var allowed = BranchAvailable && !IsSpriteConversation && !IsViewingRun && !IsBranchFinished && !IsBranchLocked;
        var depth = CurrentBranchState?.Depth ?? 0;
        var depthFull = depth >= BranchLimits.MaxDepth;
        var tip = IsResponding ? Strings.Branch_StartBusyTip : depthFull ? Strings.Branch_StartDepthTip : Strings.Branch_StartTip;
        foreach (var item in Messages)
        {
            var show = allowed && item.IsAssistant && !item.IsMarker && item.HasText;
            item.ShowBranchAction = show;
            item.CanBranch = show && !IsResponding && !depthFull && item.MessageId is not null;
            item.BranchTip = tip;
        }
    }

    /// <summary>路徑列重算（<see cref="ApplyBranchActions"/> 之後）。</summary>
    private void RefreshBranchBar() =>
        BranchBar.Apply(CurrentBranchState, _lockedChildTitle, canMerge: Messages.Any(m => !m.IsMarker), viewingRun: IsViewingRun);

    /// <summary>
    /// 訊息列表裡的支線資料（出處卡片、摘要卡）：支線的出處與直接下一層帶回的摘要（<see cref="ConversationTimeline.Build"/> 的 branches 參數）。
    /// </summary>
    private async Task<BranchTimelineInfo> BuildBranchTimelineAsync()
    {
        if (CurrentBranchState is not { } state)
            return BranchTimelineInfo.None;

        var origin = state.Current is { } current
            ? new BranchOriginInfo(state.Depth > 1 ? state.Path[^2].Title : null, current.ForkMessageAt, current.ForkExcerpt, current.Quote)
            : null;
        var cards = new List<BranchCardInfo>();
        foreach (var child in state.Children.Where(c => c.Status == BranchStatus.Merged))
        {
            var title = (await _store.GetConversationAsync(child.ConversationId))?.Title ?? BranchLimits.DefaultTitle;
            var messages = (await _store.GetMessagesAsync(child.ConversationId)).Count;
            var children = _branchStore is null ? 0 : (await _branchStore.ListChildrenAsync(child.ConversationId)).Count;
            cards.Add(new BranchCardInfo(child.ConversationId, title, child.Summary ?? "", child.CreatedAt, child.EndedAt ?? child.CreatedAt,
                child.ForkMessageAt, messages, children, child.DeliveredMessageId));
        }
        return new BranchTimelineInfo(origin, cards);
    }

    // ---- IBranchBarHost（路徑列的按鈕）----

    Task IBranchBarHost.MergeAsync(bool all) => MergeBranchAsync(all);

    Task IBranchBarHost.AbandonAsync() => AbandonBranchAsync();

    Task IBranchBarHost.BackAsync() => BackFromBranchAsync();

    Task IBranchBarHost.ReturnToBranchAsync() => ReturnToBranchAsync();

    Task IBranchBarHost.OpenConversationByIdAsync(string conversationId) => OpenConversationByIdAsync(conversationId);
}
