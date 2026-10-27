using System.ComponentModel;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Sprites;

/// <summary>
/// 把共用的對話（<see cref="IChatSession"/>）接到小精靈：事件餵給 <see cref="SpriteStateMapper"/> 與
/// <see cref="SpriteBubbleViewModel"/>、輸入列的送出與停止、氣泡上的重試與「開啟設定」（缺金鑰時存好後自動重試）。
/// <b>角色不知道對話</b>：它只會收到殼從 <see cref="SpriteStateMapper"/> 轉過去的狀態。
/// 只處理小精靈模式的回應；工作台模式發起的回應（小精靈是隱藏的）不會碰氣泡。
/// 語音（M6 6E）：小精靈開始的錄音，結果在這裡決定送出或放進輸入框；語音狀態餵給 <see cref="SpriteStateMapper"/>
/// （Listening／Transcribing／Error）；失敗與提示顯示在氣泡。
/// </summary>
public sealed class SpriteConversationController
{
    private readonly IChatSession _session;
    private readonly SpriteStateMapper _mapper;
    private readonly SpriteBubbleViewModel _bubble;
    private readonly IToolApprovalResponder _approvals;
    private readonly SpriteWindow _window;
    private readonly IAppModeCoordinator _modes;
    private readonly IApiKeyPrompt _keyPrompt;
    private readonly ISettingsWindowService _settings;
    private readonly ILogger<SpriteConversationController> _logger;
    private readonly IVoiceInputController _voice;

    private bool _spriteRun;
    private ChatFailure? _lastFailure;
    private string? _pendingQuote;

    public SpriteConversationController(
        IChatSession session, SpriteStateMapper mapper, SpriteBubbleViewModel bubble, IToolApprovalResponder approvals, SpriteWindow window,
        IAppModeCoordinator modes, IApiKeyPrompt keyPrompt, ISettingsWindowService settings, ILogger<SpriteConversationController> logger,
        IVoiceInputController? voice = null)
    {
        _session = session;
        _mapper = mapper;
        _bubble = bubble;
        _approvals = approvals;
        _window = window;
        _modes = modes;
        _keyPrompt = keyPrompt;
        _settings = settings;
        _logger = logger;
        _voice = voice ?? NoVoiceInputController.Instance;
    }

    public void Attach()
    {
        _session.RunStarted += OnRunStarted;
        _session.EventReceived += OnEvent;
        _session.RunFinished += OnRunFinished;
        _approvals.Resolved += OnApprovalResolved;

        _window.InputSubmitted += text => _ = SendAsync(text);
        _window.StopRequested += () => _session.Stop();
        _bubble.RetryRequested += () => _ = RetryAsync();
        _bubble.OpenSettingsRequested += () => _ = OpenSettingsAsync();
        _bubble.OpenWorkbenchRequested += callId => _modes.OpenWorkbenchForSpriteConversation(callId);   // 氣泡指的是小精靈那則對話（M22）

        _voice.PropertyChanged += OnVoicePropertyChanged;
        _voice.Transcribed += OnVoiceTranscribed;
        _bubble.VoiceRetryRequested += () => _ = _voice.RetryAsync();
        _bubble.VoiceDiscardRequested += () => _voice.Discard();
    }

    // ---- 會話 → 氣泡與角色 ----

    private void OnRunStarted(object? sender, RunStartedArgs args)
    {
        _spriteRun = args.Mode == ChatMode.Sprite;
        if (!_spriteRun)
            return;
        _lastFailure = null;
        _window.SetResponding(true);
        _mapper.OnRunStarted();
        _bubble.OnRunStarted(_pendingQuote);
    }

    private void OnEvent(object? sender, ChatEvent chatEvent)
    {
        if (!_spriteRun)
            return;
        if (chatEvent is Failed failed)
            _lastFailure = failed.Failure;
        _mapper.OnEvent(chatEvent);
        _bubble.OnEvent(chatEvent);
    }

    private void OnRunFinished(object? sender, RunFinishedArgs args)
    {
        if (!_spriteRun)
            return;
        _spriteRun = false;
        _window.SetResponding(false);
        _mapper.OnRunFinished(args.Result);
        _bubble.OnRunFinished(args.Result);
    }

    /// <summary>權限確認在任一邊被回答（氣泡或工作台的卡片）：氣泡與角色狀態都跟著更新。</summary>
    private void OnApprovalResolved(object? sender, ApprovalResolved resolved)
    {
        _mapper.OnApprovalResolved(resolved.CallId);
        _bubble.OnApprovalResolved(resolved.CallId, resolved.Decision);
    }

    // ---- 語音 ----

    private void OnVoicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IVoiceInputController.State):
                OnVoiceStateChanged();
                break;
            case nameof(IVoiceInputController.Notice):
                if (_voice.Notice is { } notice && _voice.Origin == VoiceOrigin.Sprite)
                    _bubble.ShowNotice(notice);
                break;
        }
    }

    /// <summary>狀態機 → 光球（對話優先，見 <see cref="SpriteStateMapper.OnVoiceState"/>）；失敗的氣泡（小精靈開始的錄音才顯示）。</summary>
    private void OnVoiceStateChanged()
    {
        var state = _voice.State;
        _mapper.OnVoiceState(state);

        if (state == VoiceInputState.Failed && _voice.Origin == VoiceOrigin.Sprite && !_spriteRun)
            _bubble.ShowVoiceFailure(SpriteVoiceBarViewModel.FailureSentence(_voice.LastFailureMessage));
        else
            _bubble.HideVoiceFailure();   // 重試、放棄、換對話或取消：失敗已經處理
    }

    /// <summary>
    /// 轉寫完成（只處理小精靈開始的）。送出的條件與工作台相同（計畫 6D）：同一則對話、沒有在回應、小精靈顯示中、輸入框是空的，
    /// 才走送出路徑，並先以小字引用轉寫結果；其他情況放進輸入框（停止並送出被擋下時給提示）。
    /// </summary>
    private void OnVoiceTranscribed(object? sender, VoiceTranscribedArgs args)
    {
        if (args.Origin != VoiceOrigin.Sprite)
            return;   // 工作台開始的錄音由工作台處理
        _ = HandleVoiceTranscribedAsync(args);
    }

    private async Task HandleVoiceTranscribedAsync(VoiceTranscribedArgs args)
    {
        var sameConversation = args.ConversationId is not null && args.ConversationId == _session.ConversationId;
        var responding = _session.IsResponding;
        var shown = _window.IsShown;
        var hadDraft = !string.IsNullOrWhiteSpace(_window.InputText);

        if (args.Action == VoiceStopAction.Send && sameConversation && !responding && shown && !hadDraft)
        {
            _pendingQuote = args.Text;
            try
            {
                await SendAsync(args.Text);
            }
            finally
            {
                _pendingQuote = null;
            }
            return;
        }

        _window.InsertVoiceDraft(args.Text);
        if (args.Action == VoiceStopAction.Send)
        {
            if (!sameConversation || responding)
                _bubble.ShowNotice(Strings.Voice_NoticeEditOnly);
            else if (hadDraft)
                _bubble.ShowNotice(Strings.Voice_NoticeHasDraft);
        }
    }

    // ---- 使用者操作 ----

    private async Task SendAsync(string text)
    {
        if (_session.IsResponding)
            return;   // 回應中只能打草稿
        if (_session.ActiveMode != ChatMode.Sprite && !_session.TrySetMode(ChatMode.Sprite))
            return;
        await RunAsync(() => _session.SendAsync(text));
    }

    private async Task RetryAsync()
    {
        if (_session.IsResponding)
            return;
        await RunAsync(() => _session.RetryAsync());
    }

    /// <summary>缺少、無效或讀不出來的金鑰：開啟設定，存好後自動重試（和工作台相同）；其他問題只開設定。</summary>
    private async Task OpenSettingsAsync()
    {
        try
        {
            if (ToPromptReason(_lastFailure?.Kind) is { } reason)
            {
                if (await _keyPrompt.PromptAsync(reason))
                    await RetryAsync();
            }
            else
            {
                await _settings.ShowAsync(new SettingsNavigation(SettingsSection.Connections));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "從小精靈開啟設定失敗");
        }
    }

    private async Task RunAsync(Func<Task<ChatRunResult>> run)
    {
        try
        {
            await run();   // 結果已經由 RunFinished 事件交給氣泡與角色
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "小精靈無法送出訊息");   // 沒有對話或剛好開始回應：忽略
        }
    }

    private static ApiKeyPromptReason? ToPromptReason(ChatFailureKind? kind) => kind switch
    {
        ChatFailureKind.MissingApiKey => ApiKeyPromptReason.Missing,
        ChatFailureKind.InvalidApiKey => ApiKeyPromptReason.Invalid,
        ChatFailureKind.SecretUnreadable => ApiKeyPromptReason.Unreadable,
        _ => null,
    };
}
