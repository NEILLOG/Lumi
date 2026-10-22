using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Speech;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 語音輸入的狀態機（見 plan/06-speech-whisper.md 6D）：
/// Idle ─Start→ Recording ─StopEdit／StopSend／達 90 秒→ Transcribing ─成功→ Idle（結果由 <see cref="Transcribed"/> 交給呼叫端）；
/// Recording ─Cancel→ Idle；Transcribing ─Cancel→ Idle（取消請求、丟掉錄音）；Transcribing ─失敗→ Failed（錄音保留）─Retry→ Transcribing；Failed ─Discard→ Idle。
/// 過期的非同步結果用 <see cref="_generation"/> 辨認：每次開始或取消都會換新的世代，舊世代的結果直接丟掉。
/// </summary>
public sealed partial class VoiceInputController : ObservableObject, IVoiceInputController, IAsyncDisposable
{
    /// <summary>計時器的更新間隔（約 100–250 ms）。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>音量節流：兩次更新至少相隔這麼久。</summary>
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(100);

    private readonly IAudioRecorder _recorder;
    private readonly ISpeechToText _speech;
    private readonly IModelCatalog _catalog;
    private readonly IChatSession _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly ILogger<VoiceInputController> _logger;

    private readonly EventHandler<float> _onLevel;
    private readonly EventHandler _onMaxDuration;
    private readonly EventHandler<ConversationChangedArgs> _onConversationChanged;
    private readonly EventHandler<RunStartedArgs> _onRunStarted;
    private readonly EventHandler<RunFinishedArgs> _onRunFinished;
    private readonly EventHandler<AppSettings> _onCatalogChanged;

    private VoiceInputState _state = VoiceInputState.Idle;
    private TimeSpan? _recordedDuration;
    private double _level;
    private string? _lastFailure;
    private string? _notice;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastLevelAt;
    private ITimer? _ticker;
    private int _generation;
    private bool _starting;
    private bool _disposed;

    // 目前這段錄音（或失敗保留的錄音）的資料
    private RecordedAudio? _audio;
    private string? _conversationId;
    private VoiceStopAction _action;
    private VoiceOrigin? _origin;
    private CancellationTokenSource? _transcribeCts;

    public VoiceInputController(
        IAudioRecorder recorder,
        ISpeechToText speech,
        IModelCatalog catalog,
        IChatSession session,
        IUiDispatcher dispatcher,
        TimeProvider time,
        ILogger<VoiceInputController> logger)
    {
        _recorder = recorder;
        _speech = speech;
        _catalog = catalog;
        _session = session;
        _dispatcher = dispatcher;
        _time = time;
        _logger = logger;

        // 錄音器的事件在原生執行緒發出，轉到 UI 執行緒再處理
        _onLevel = (_, level) => _dispatcher.Post(() => OnLevel(level));
        _onMaxDuration = (_, _) => _dispatcher.Post(OnMaxDuration);
        _onConversationChanged = (_, _) => { CancelIfBusy(); NotifyAvailability(); };
        _onRunStarted = (_, _) => { if (_state == VoiceInputState.Recording) Cancel(); NotifyAvailability(); };
        _onRunFinished = (_, _) => NotifyAvailability();
        _onCatalogChanged = (_, _) => NotifyAvailability();

        _recorder.LevelChanged += _onLevel;
        _recorder.MaxDurationReached += _onMaxDuration;
        _session.ConversationChanged += _onConversationChanged;
        _session.RunStarted += _onRunStarted;
        _session.RunFinished += _onRunFinished;
        _catalog.Changed += _onCatalogChanged;
    }

    public event EventHandler<VoiceTranscribedArgs>? Transcribed;

    public VoiceInputState State => _state;

    /// <summary>錄音中的長度即時計算（計時器只負責通知畫面）。</summary>
    public TimeSpan Elapsed => _state == VoiceInputState.Recording ? _time.GetUtcNow() - _startedAt : TimeSpan.Zero;

    public TimeSpan? RecordedDuration => _recordedDuration;

    public double Level => _level;

    public string? LastFailureMessage => _lastFailure;

    public string? Notice => _notice;

    public bool CanStart => DisabledReason is null;

    /// <summary>依序檢查：忙碌中、端點、Token（只看 hint 與綁定的主機，不碰 ISecretStore，避免 macOS 鑰匙圈授權視窗）、回應中。</summary>
    public VoiceOrigin? Origin => _origin;

    /// <summary>另一邊正在錄音或轉寫時，原因是「另一個視窗正在語音輸入」；其餘同 <see cref="DisabledReason"/>。</summary>
    public string? DisabledReasonFor(VoiceOrigin origin)
    {
        if ((_state is VoiceInputState.Recording or VoiceInputState.Transcribing) && _origin is { } owner && owner != origin)
            return Strings.Voice_OtherWindow;
        return DisabledReason;
    }

    public string? DisabledReason
    {
        get
        {
            if (_state == VoiceInputState.Failed)
                return Strings.Voice_FailedBlocked;
            if (_state != VoiceInputState.Idle || _starting)
                return Strings.Voice_BusyReason;

            var speech = _catalog.Current.Speech.Normalize();
            if (speech.Endpoint.Length == 0)
                return Strings.Voice_NotConfigured;
            var check = SpeechEndpointRules.Validate(speech.Endpoint);
            if (!check.IsValid)
                return Strings.Voice_EndpointInvalid;
            if (speech.TokenHint is null || speech.TokenHost is null
                || !string.Equals(speech.TokenHost, check.Uri!.Host, StringComparison.OrdinalIgnoreCase))
                return Strings.Voice_TokenMissing;

            return _session.IsResponding ? Strings.Voice_RespondingBlocked : null;
        }
    }

    public async Task StartAsync(VoiceOrigin origin, CancellationToken ct = default)
    {
        if (!CanStart || _disposed)
            return;

        SetNotice(null);
        _lastFailure = null;
        _origin = origin;
        _starting = true;
        var generation = _generation;
        var conversationId = _session.ConversationId;
        var started = false;
        try
        {
            await _recorder.StartAsync(ct);
            started = true;
        }
        catch (AudioRecorderException ex)
        {
            SetNotice(ex.Message);
        }
        finally
        {
            _starting = false;
            NotifyAvailability();
        }
        if (!started)
            return;

        // 開始途中被取消（或離開）：錄音器剛打開，馬上關掉
        if (generation != _generation || _disposed)
        {
            _recorder.Cancel();
            return;
        }

        _conversationId = conversationId;
        _startedAt = _time.GetUtcNow();
        _lastLevelAt = default;
        _ticker = _time.CreateTimer(_ => _dispatcher.Post(OnTick), null, TickInterval, TickInterval);
        SetState(VoiceInputState.Recording);
    }

    public async Task StopAsync(VoiceStopAction action)
    {
        if (_state != VoiceInputState.Recording)
            return;

        var generation = _generation;
        _action = action;
        SetState(VoiceInputState.Transcribing);   // 停止的瞬間就換成轉寫中的畫面（等錄音器交出結果需要一點時間）

        RecordedAudio audio;
        try
        {
            audio = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            _logger.LogError(ex, "停止錄音時發生未預期的錯誤");
            SetNotice(Strings.Voice_CaptureUnexpected);
            SetState(VoiceInputState.Idle);
            return;
        }

        if (generation != _generation)
            return;   // 停止途中被取消，錄音已經丟掉

        _recordedDuration = audio.Duration;
        if (audio.TooShort)
        {
            SetNotice(Strings.Voice_TooShort);
            SetState(VoiceInputState.Idle);
            return;
        }
        if (audio.LooksSilent)
        {
            SetNotice(Strings.Voice_Silent);
            SetState(VoiceInputState.Idle);
            return;
        }

        _audio = audio;
        await RunTranscriptionAsync();
    }

    public void Cancel()
    {
        switch (_state)
        {
            case VoiceInputState.Recording:
                _generation++;
                _recorder.Cancel();
                DropAudio();
                SetState(VoiceInputState.Idle);
                break;
            case VoiceInputState.Transcribing:
                _generation++;
                _transcribeCts?.Cancel();
                DropAudio();
                SetState(VoiceInputState.Idle);
                break;
            case VoiceInputState.Failed:
                Discard();
                break;
            default:
                if (_starting)
                {
                    _generation++;   // StartAsync 回來時看到世代不同，會把錄音器關掉
                    Notify(nameof(DisabledReason), nameof(CanStart));
                }
                break;
        }
    }

    public Task RetryAsync()
    {
        if (_state != VoiceInputState.Failed || _audio is null)
            return Task.CompletedTask;

        _generation++;
        _lastFailure = null;
        return RunTranscriptionAsync();
    }

    public void Discard()
    {
        if (_state != VoiceInputState.Failed)
            return;

        _generation++;
        DropAudio();
        SetState(VoiceInputState.Idle);
    }

    public void ClearNotice() => SetNotice(null);

    /// <summary>用保留的錄音轉寫。成功回到 Idle 並發出 <see cref="Transcribed"/>；失敗進 Failed（保留錄音）；被取消就靜默。</summary>
    private async Task RunTranscriptionAsync()
    {
        var audio = _audio!;
        var generation = _generation;
        var conversationId = _conversationId;
        var action = _action;
        var origin = _origin ?? VoiceOrigin.Workbench;
        var cts = new CancellationTokenSource();
        _transcribeCts = cts;
        SetState(VoiceInputState.Transcribing);

        try
        {
            var result = await _speech.TranscribeAsync(new MemoryStream(audio.Wav), "voice.wav", conversationId, cts.Token);
            if (generation != _generation)
                return;

            var text = (result.Text ?? "").Trim();
            DropAudio();
            if (text.Length == 0)
            {
                SetNotice(Strings.Voice_NoSpeech);
                SetState(VoiceInputState.Idle);
                return;
            }

            SetState(VoiceInputState.Idle);
            _dispatcher.Post(() => Transcribed?.Invoke(this, new VoiceTranscribedArgs(text, action, conversationId, result.Language, origin)));
        }
        catch (OperationCanceledException) when (generation != _generation || cts.IsCancellationRequested)
        {
            // 使用者取消：靜默回到閒置（Cancel 已經處理狀態）
        }
        catch (SpeechFailure ex)
        {
            if (generation != _generation)
                return;
            _lastFailure = ex.Message;
            SetState(VoiceInputState.Failed);
        }
        catch (Exception ex)
        {
            if (generation != _generation)
                return;
            // 詳細只寫 log；訊息不含 Token、不含伺服器內文
            _logger.LogError(ex, "語音轉寫發生未預期的錯誤");
            _lastFailure = Strings.Voice_TranscribeUnexpected;
            SetState(VoiceInputState.Failed);
        }
    }

    private void OnLevel(float level)
    {
        if (_state != VoiceInputState.Recording)
            return;
        var now = _time.GetUtcNow();
        if (now - _lastLevelAt < LevelInterval)
            return;
        _lastLevelAt = now;
        _level = Math.Clamp(level, 0, 1);
        Notify(nameof(Level));
    }

    private void OnTick()
    {
        if (_state == VoiceInputState.Recording)
            Notify(nameof(Elapsed));
    }

    private void OnMaxDuration()
    {
        // 到 90 秒照「停止並編輯」處理（計畫 6D）
        if (_state == VoiceInputState.Recording)
            _ = StopAsync(VoiceStopAction.Edit);
    }

    private void CancelIfBusy()
    {
        if (_state != VoiceInputState.Idle || _starting)
            Cancel();
    }

    private void NotifyAvailability() => Notify(nameof(CanStart), nameof(DisabledReason));

    private void SetState(VoiceInputState next)
    {
        _state = next;
        if (next != VoiceInputState.Recording)
        {
            _ticker?.Dispose();
            _ticker = null;
            _level = 0;
        }
        if (next == VoiceInputState.Idle)
        {
            _recordedDuration = null;
            _lastFailure = null;
        }

        Notify(nameof(State), nameof(Elapsed), nameof(Level), nameof(RecordedDuration), nameof(LastFailureMessage),
            nameof(CanStart), nameof(DisabledReason));
    }

    private void SetNotice(string? text)
    {
        _notice = text;
        Notify(nameof(Notice));
    }

    private void DropAudio()
    {
        _audio = null;
        _conversationId = null;
        _transcribeCts = null;
    }

    /// <summary>屬性變更一律經 UI 執行緒發出（UI 執行緒上 Post 會直接執行）。</summary>
    private void Notify(params string[] names) => _dispatcher.Post(() =>
    {
        foreach (var name in names)
            OnPropertyChanged(name);
    });

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        Cancel();   // 錄音中會關掉麥克風
        _disposed = true;
        _recorder.LevelChanged -= _onLevel;
        _recorder.MaxDurationReached -= _onMaxDuration;
        _session.ConversationChanged -= _onConversationChanged;
        _session.RunStarted -= _onRunStarted;
        _session.RunFinished -= _onRunFinished;
        _catalog.Changed -= _onCatalogChanged;
        _ticker?.Dispose();
        _ticker = null;
        return ValueTask.CompletedTask;
    }
}
