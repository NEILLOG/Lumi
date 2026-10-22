using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools;

namespace Lumi.App.Sprites;

/// <summary>氣泡的種類。<see cref="Notice"/> 是語音的短暫提示（太短、沒有聲音、沒聽到內容），會自動淡出。</summary>
public enum SpriteBubbleKind { Hidden, Status, Streaming, Done, Approval, Error, Notice }

/// <summary>氣泡內的權限確認（ui-spec 8.3）：預設精簡，「更多選項」在氣泡內展開。</summary>
public sealed partial class SpriteApprovalViewModel(ToolApprovalRequest request) : ObservableObject
{
    public string CallId => request.CallId;

    public string DisplayName => request.DisplayName;

    public bool IsDestructive => request.Permission == ToolPermission.Destructive;

    public string Question => string.Format(
        CultureInfo.CurrentCulture, IsDestructive ? Strings.Sprite_DestructiveQuestion : Strings.Sprite_ApprovalQuestion, request.DisplayName);

    /// <summary>對象（路徑或網址），用等寬字顯示。</summary>
    public string? Target => request.Preview.Target;

    public bool HasTarget => !string.IsNullOrEmpty(Target);

    /// <summary><c>Destructive</c> 要完整列出所有對象。</summary>
    public string? Detail => request.Preview.Detail;

    public bool ShowDetail => IsDestructive && !string.IsNullOrEmpty(Detail);

    public string AllowLabel => IsDestructive ? Strings.Sprite_AllowDestructive : Strings.Sprite_Allow;

    /// <summary><c>Destructive</c> 只有「允許刪除」「拒絕」，沒有更多選項。</summary>
    public bool CanExpand => !IsDestructive;

    public bool ShowMoreLink => CanExpand && !IsExpanded;

    public bool ShowOptions => CanExpand && IsExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMoreLink), nameof(ShowOptions))]
    private bool _isExpanded;
}

/// <summary>
/// 小精靈的氣泡（純邏輯，計時用 <see cref="TimeProvider"/>），依 ui-spec 8.1：
/// 同一時間只顯示<b>最新一則</b>、不顯示使用者的問題；思考或呼叫工具時只有一行狀態；回覆串流寫進氣泡；
/// 完成後停留數秒淡出（游標停在上面時暫停）；等待確認與錯誤不自動淡出；
/// 有完整內容時（呼叫了 <c>show_details</c>、回答被截斷、或達到輸出上限）底下出現「在工作台開啟完整內容 →」。
/// 語音（M6 6E）：送出時先以小字引用轉寫結果（<see cref="QuoteText"/>）再進入思考；語音提示用 <see cref="ShowNotice"/>
/// （自動淡出，不蓋過權限確認與錯誤）；語音轉寫失敗用 <see cref="ShowVoiceFailure"/>（和錯誤氣泡一樣不自動淡出）。
/// </summary>
public sealed partial class SpriteBubbleViewModel : ObservableObject
{
    private readonly SpriteShellOptions _options;
    private readonly TimeProvider _time;
    private readonly IToolApprovalResponder _approvals;

    private string _fullText = "";
    private bool _startNewSegment;
    private string? _detailsCallId;
    private bool _detailsCompleted;
    private bool _maxTokens;
    private bool _hovered;
    private TimeSpan _lingered;
    private DateTimeOffset _lastTick;
    private ChatFailure? _failure;

    public SpriteBubbleViewModel(SpriteShellOptions options, TimeProvider time, IToolApprovalResponder? approvals = null)
    {
        _options = options;
        _time = time;
        _approvals = approvals ?? NullToolApprovalResponder.Instance;
    }

    /// <summary>點「在工作台開啟完整內容」或「在工作台檢視／查看」；帶著要捲到的工具呼叫 id（可能沒有）。</summary>
    public event Action<string?>? OpenWorkbenchRequested;

    /// <summary>錯誤氣泡的「重試」。</summary>
    public event Action? RetryRequested;

    /// <summary>錯誤是缺少或無效的金鑰時，「開啟設定」。</summary>
    public event Action? OpenSettingsRequested;

    /// <summary>語音轉寫失敗的「重試轉寫」。</summary>
    public event Action? VoiceRetryRequested;

    /// <summary>語音轉寫失敗的「放棄這段錄音」。</summary>
    public event Action? VoiceDiscardRequested;

    /// <summary>錯誤氣泡被關掉（讓角色離開 <c>Error</c> 狀態）。</summary>
    public event Action? Dismissed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsStatus), nameof(IsAnswer), nameof(IsApproval), nameof(IsError), nameof(IsNotice), nameof(IsInteractive))]
    private SpriteBubbleKind _kind = SpriteBubbleKind.Hidden;

    public bool IsVisible => Kind != SpriteBubbleKind.Hidden;

    public bool IsStatus => Kind == SpriteBubbleKind.Status;

    public bool IsNotice => Kind == SpriteBubbleKind.Notice;

    public bool IsAnswer => Kind is SpriteBubbleKind.Streaming or SpriteBubbleKind.Done;

    public bool IsApproval => Kind == SpriteBubbleKind.Approval;

    public bool IsError => Kind == SpriteBubbleKind.Error;

    /// <summary>有按鈕或連結：這時氣泡要能點，不能讓滑鼠穿透。</summary>
    public bool IsInteractive => Kind is SpriteBubbleKind.Approval or SpriteBubbleKind.Error || ShowDetailsLink;

    /// <summary>一行狀態（思考中…、讀取檔案 · plan/00-master-plan.md）。沿用工具卡片標題的「動作 · 對象」。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>語音送出時的引用（小字，已截斷）；開始回應後清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuote))]
    private string? _quoteText;

    public bool HasQuote => QuoteText is not null;

    /// <summary>語音提示的文字（<see cref="SpriteBubbleKind.Notice"/>）。</summary>
    [ObservableProperty]
    private string _noticeText = "";

    /// <summary>目前的錯誤是語音轉寫失敗：按鈕是「重試轉寫」「放棄這段錄音」，而不是聊天的重試。</summary>
    [ObservableProperty]
    private bool _isVoiceFailure;

    /// <summary>目前顯示的回答（已依字數上限截斷）。</summary>
    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool _showDetailsLink;

    [ObservableProperty]
    private SpriteApprovalViewModel? _approval;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _canRetry;

    [ObservableProperty]
    private bool _canOpenSettings;

    public bool IsTruncated { get; private set; }

    // ---- 對話事件 ----

    /// <param name="quote">語音送出的轉寫結果：先以小字顯示在思考行上方。</param>
    public void OnRunStarted(string? quote = null)
    {
        _fullText = "";
        _startNewSegment = false;
        _detailsCallId = null;
        _detailsCompleted = false;
        _maxTokens = false;
        _failure = null;
        IsTruncated = false;
        Text = "";
        Approval = null;
        ShowDetailsLink = false;
        IsVoiceFailure = false;
        QuoteText = quote is null ? null : Truncate(quote, QuoteMaxClusters).Text;
        ShowStatus(Strings.Sprite_StatusThinking);
    }

    public void OnEvent(ChatEvent chatEvent)
    {
        switch (chatEvent)
        {
            case ThinkingDelta when Kind != SpriteBubbleKind.Approval:
                ShowStatus(Strings.Sprite_StatusThinking);
                break;
            case TextDelta delta:
                AppendText(delta.Text);
                break;
            case ToolCallStarted started:
                _startNewSegment = true;   // 呼叫工具之前的話不算「最新一則」，之後的回答才是
                if (started.ToolName == ShowDetailsTool.Name)
                    _detailsCallId = started.CallId;
                ShowStatus(Title(started.DisplayName, started.Target));
                break;
            case ApprovalRequired required:
                Approval = new SpriteApprovalViewModel(required.Request);
                Kind = SpriteBubbleKind.Approval;
                break;
            case ToolCallCompleted completed:
                if (completed.CallId == _detailsCallId && !completed.Result.IsError)
                {
                    _detailsCompleted = true;
                    ShowDetailsLink = true;
                }
                if (Kind != SpriteBubbleKind.Approval)
                    ShowStatus(Strings.Sprite_StatusThinking);
                break;
            case Completed { Reason: StopReason.ToolUse }:
                break;
            case Completed completed:
                Finish(completed);
                break;
            case Failed failed:
                _failure = failed.Failure;
                break;
        }
    }

    /// <summary>權限確認被回答（氣泡或工作台）：氣泡回到工具執行狀態（「已允許 · 寫入檔案中…」）。</summary>
    public void OnApprovalResolved(string callId, ApprovalDecision decision)
    {
        if (Approval is not { } approval || approval.CallId != callId)
            return;
        Approval = null;
        ShowStatus(string.Format(
            CultureInfo.CurrentCulture,
            decision == ApprovalDecision.Deny ? Strings.Sprite_StatusDenied : Strings.Sprite_StatusAllowed,
            approval.DisplayName));
    }

    public void OnRunFinished(ChatRunResult result)
    {
        if (result.Unexpected || result.Failure is not null)
        {
            ShowError(result);
            return;
        }

        // 使用者停止、或串流沒有正常結束：沒有東西可以停留
        if (result.Cancelled || Kind is not SpriteBubbleKind.Done)
        {
            Approval = null;
            Hide();
        }
    }

    private void ShowError(ChatRunResult result)
    {
        Approval = null;
        IsVoiceFailure = false;
        var failure = result.Failure ?? _failure;
        ErrorText = result.Unexpected || failure is null ? Strings.Workbench_UnexpectedError : failure.Message;
        CanRetry = failure is null || failure.Retryable || failure.Kind is ChatFailureKind.MissingApiKey or ChatFailureKind.InvalidApiKey or ChatFailureKind.SecretUnreadable;
        CanOpenSettings = failure?.Kind is ChatFailureKind.MissingApiKey or ChatFailureKind.InvalidApiKey or ChatFailureKind.SecretUnreadable
            or ChatFailureKind.ModelNotConfigured;
        Kind = SpriteBubbleKind.Error;
    }

    // ---- 計時：完成後淡出，游標停在上面時暫停 ----

    public void SetHovered(bool hovered) => _hovered = hovered;

    /// <summary>每個輪詢週期呼叫。</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var elapsed = _lastTick == default ? TimeSpan.Zero : now - _lastTick;
        _lastTick = now;

        if (Kind == SpriteBubbleKind.Notice)
        {
            if (!_hovered)
                _lingered += elapsed;
            if (_lingered >= _options.NoticeLinger)
                Kind = SpriteBubbleKind.Hidden;   // 語音提示不是錯誤，淡出時不通知角色
            return;
        }

        if (Kind != SpriteBubbleKind.Done)
            return;
        if (!_hovered)
            _lingered += elapsed;
        if (_lingered >= _options.BubbleLinger)
            Hide();
    }

    // ---- 使用者操作 ----

    [RelayCommand]
    private void AllowOnce() => Resolve(ApprovalDecision.AllowOnce);

    [RelayCommand]
    private void AllowForConversation() => Resolve(ApprovalDecision.AllowForConversation);

    [RelayCommand]
    private void Deny() => Resolve(ApprovalDecision.Deny);

    private void Resolve(ApprovalDecision decision)
    {
        if (Approval is { } approval)
            _approvals.Resolve(approval.CallId, decision);   // 發出 Resolved：氣泡與工作台的卡片一起更新
    }

    [RelayCommand]
    private void ToggleMoreOptions()
    {
        if (Approval is { CanExpand: true } approval)
            approval.IsExpanded = !approval.IsExpanded;
    }

    /// <summary>「在工作台檢視」（確認）、「在工作台查看」（錯誤）：以查看中狀態開啟工作台，確認時捲到那張工具卡片。</summary>
    [RelayCommand]
    private void ViewInWorkbench() => OpenWorkbenchRequested?.Invoke(Approval?.CallId);

    /// <summary>「在工作台開啟完整內容 →」：由使用者自己點，不自動打開。</summary>
    [RelayCommand]
    private void OpenDetails() => OpenWorkbenchRequested?.Invoke(_detailsCompleted ? _detailsCallId : null);

    [RelayCommand]
    private void Retry()
    {
        Hide();
        RetryRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    private void VoiceRetry() => VoiceRetryRequested?.Invoke();

    [RelayCommand]
    private void VoiceDiscard() => VoiceDiscardRequested?.Invoke();

    /// <summary>
    /// 語音提示（太短、沒有聲音、沒聽到內容、回應中改放進輸入框）：短暫的一行，自動淡出。
    /// 權限確認與錯誤正在顯示時不蓋過它（提示只是補充，確認與錯誤要能按）。
    /// </summary>
    public void ShowNotice(string text)
    {
        if (Kind is SpriteBubbleKind.Approval or SpriteBubbleKind.Error)
            return;
        NoticeText = text;
        _lingered = TimeSpan.Zero;
        _lastTick = _time.GetUtcNow();
        Kind = SpriteBubbleKind.Notice;
    }

    /// <summary>
    /// 語音轉寫失敗：一句說明加「重試轉寫」「放棄這段錄音」，不自動淡出（比照錯誤氣泡）。
    /// 等待確認時不蓋過（確認要先回答；失敗仍然在輸入列上）。
    /// </summary>
    public void ShowVoiceFailure(string message)
    {
        if (Kind == SpriteBubbleKind.Approval)
            return;
        Approval = null;
        ErrorText = message;
        CanRetry = false;
        CanOpenSettings = false;
        IsVoiceFailure = true;
        Kind = SpriteBubbleKind.Error;
    }

    /// <summary>語音失敗已經處理（重試、放棄或換了對話）：拿掉語音的錯誤氣泡。不是聊天的錯誤，不會蓋掉。</summary>
    public void HideVoiceFailure()
    {
        if (IsVoiceFailure && Kind == SpriteBubbleKind.Error)
            Hide();
    }

    /// <summary>換對話時清掉氣泡（不算使用者關掉錯誤，不通知角色）。</summary>
    public void Clear()
    {
        Approval = null;
        Text = "";
        _fullText = "";
        ShowDetailsLink = false;
        QuoteText = null;
        IsVoiceFailure = false;
        Kind = SpriteBubbleKind.Hidden;
    }

    [RelayCommand]
    private void Dismiss()
    {
        if (Kind is SpriteBubbleKind.Done or SpriteBubbleKind.Error)
            Hide();
    }

    // ---- 內部 ----

    private void ShowStatus(string text)
    {
        StatusText = text;
        Kind = SpriteBubbleKind.Status;
    }

    private void AppendText(string delta)
    {
        QuoteText = null;   // 回答開始了，引用的語音就不再需要
        if (_startNewSegment)
        {
            _fullText = "";
            _startNewSegment = false;
        }
        _fullText += delta;
        ApplyText();
        if (Kind != SpriteBubbleKind.Approval)
            Kind = SpriteBubbleKind.Streaming;
    }

    private void Finish(Completed completed)
    {
        // 沒有串流文字（一次給完）時，用最後的訊息內容
        if (_fullText.Length == 0 || _startNewSegment)
        {
            var text = string.Concat(completed.Message.Content.OfType<TextBlock>().Select(t => t.Text));
            if (text.Length > 0)
            {
                _fullText = text;
                _startNewSegment = false;
            }
        }
        _maxTokens = completed.Reason == StopReason.MaxTokens;
        ApplyText();
        _lingered = TimeSpan.Zero;
        _lastTick = _time.GetUtcNow();
        Kind = SpriteBubbleKind.Done;
    }

    /// <summary>依字形叢集截斷（不會把 emoji 或組合字切成兩半）。</summary>
    private void ApplyText()
    {
        var (shown, truncated) = Truncate(_fullText, _options.BubbleMaxClusters);
        IsTruncated = truncated;
        Text = shown;
        ShowDetailsLink = _detailsCompleted || truncated || _maxTokens;
    }

    public static (string Text, bool Truncated) Truncate(string text, int maxClusters)
    {
        if (text.Length <= maxClusters)
            return (text, false);   // 一個叢集至少一個字元：字元數不超過就一定沒超過

        var starts = StringInfo.ParseCombiningCharacters(text);   // 每個字形叢集的起點
        return starts.Length <= maxClusters ? (text, false) : (text[..starts[maxClusters]] + Strings.Sprite_Ellipsis, true);
    }

    private void Hide()
    {
        Kind = SpriteBubbleKind.Hidden;
        Dismissed?.Invoke();
    }

    /// <summary>語音引用最多幾個字形叢集（小字，兩行內）。</summary>
    private const int QuoteMaxClusters = 48;

    private static string Title(string displayName, string? target) =>
        string.IsNullOrEmpty(target) ? displayName : $"{displayName} · {target}";
}
