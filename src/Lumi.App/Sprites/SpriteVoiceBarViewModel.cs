using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;

namespace Lumi.App.Sprites;

/// <summary>
/// 小精靈輸入列的語音部分（ui-spec 8.2、8.4）：麥克風鈕的啟用與原因、收音列（收音中、轉寫中、失敗）、語音草稿的「語音」標籤。
/// 純邏輯，不碰視窗；狀態來自 <see cref="IVoiceInputController"/>（工作台與小精靈共用同一個）。
/// <para>
/// 麥克風停用的情況：沒有設定語音、回應中、另一個視窗正在錄音或轉寫（由控制器給出原因），以及小精靈暫時隱藏中
/// （<see cref="Shown"/> 為 false；隱藏時正在收音則停止收音，不在背景錄音）。
/// </para>
/// </summary>
public sealed partial class SpriteVoiceBarViewModel : ObservableObject
{
    private readonly IVoiceInputController _voice;
    private bool _shown;
    private bool _isVoiceDraft;
    private string? _draftText;

    public SpriteVoiceBarViewModel(IVoiceInputController voice)
    {
        _voice = voice;
        _voice.PropertyChanged += OnVoicePropertyChanged;
        Refresh();
    }

    // ---- 收音列 ----

    public bool IsVoiceActive => _voice.State != VoiceInputState.Idle;

    public bool IsRecording => _voice.State == VoiceInputState.Recording;

    public bool IsTranscribing => _voice.State == VoiceInputState.Transcribing;

    public bool IsFailed => _voice.State == VoiceInputState.Failed;

    /// <summary>錄音中的計時（mm:ss）。</summary>
    public string TimeText => FormatClock(_voice.Elapsed);

    /// <summary>收音列的無障礙名稱，跟著狀態（「收音中 00:12」）。</summary>
    public string RecordingName => string.Format(CultureInfo.CurrentCulture, Strings.Voice_StatusRecording, TimeText);

    public string TranscribingText => Strings.Sprite_VoiceTranscribing;

    /// <summary>失敗的一句話（含服務給的說明，不含 Token）。</summary>
    public string FailureText => FailureSentence(_voice.LastFailureMessage);

    /// <summary>失敗的一句話（收音列與氣泡共用）。</summary>
    public static string FailureSentence(string? message) => string.Format(
        CultureInfo.CurrentCulture, Strings.Sprite_VoiceFailedFormat, message ?? Strings.Voice_TranscribeUnexpected);

    public double Level => _voice.Level;

    // ---- 麥克風鈕 ----

    /// <summary>小精靈的視窗是否顯示中（<see cref="SpriteWindow"/> 隨 IsVisible 設定）。</summary>
    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value)
                return;
            _shown = value;
            // 隱藏時不在背景錄音：收音中（而且是小精靈開始的）就丟掉；轉寫中照常完成
            if (!value && IsRecording && _voice.Origin == VoiceOrigin.Sprite)
                _voice.Cancel();
            Refresh();
        }
    }

    [ObservableProperty]
    private bool _micEnabled;

    /// <summary>麥克風鈕的 Tooltip：停用時是原因（ShowOnDisabled），可用時是「語音輸入」。</summary>
    [ObservableProperty]
    private string _micTip = Strings.Composer_Mic;

    [RelayCommand(CanExecute = nameof(CanStartVoice))]
    private Task StartVoiceAsync() => _voice.StartAsync(VoiceOrigin.Sprite);

    private bool CanStartVoice() => MicEnabled;

    // ---- 收音列的命令 ----

    /// <summary>「停止並編輯」：文字留在輸入列（不送出）。</summary>
    [RelayCommand]
    private Task StopEditAsync() => _voice.StopAsync(VoiceStopAction.Edit);

    /// <summary>「停止並送出」：轉寫完成後由小精靈的規則決定送出或放進輸入框。</summary>
    [RelayCommand]
    private Task StopSendAsync() => _voice.StopAsync(VoiceStopAction.Send);

    [RelayCommand]
    private void Cancel() => _voice.Cancel();

    [RelayCommand]
    private Task RetryAsync() => _voice.RetryAsync();

    [RelayCommand]
    private void Discard() => _voice.Discard();

    /// <summary>Esc：只在錄音中取消（ui-spec 6.3）；其他狀態不處理。</summary>
    public bool CancelRecordingIfAny()
    {
        if (!IsRecording)
            return false;
        _voice.Cancel();
        return true;
    }

    // ---- 語音草稿（輸入框的文字是語音轉寫來的） ----

    /// <summary>輸入框的文字是語音轉寫來的：輸入框前面顯示「語音」標籤，使用者改動或清空後拿掉。</summary>
    public bool IsVoiceDraft => _isVoiceDraft;

    public void MarkDraft(string text)
    {
        _draftText = text;
        _isVoiceDraft = true;
        OnPropertyChanged(nameof(IsVoiceDraft));
    }

    /// <summary>輸入框的文字變了：和標記的草稿不同就拿掉標籤。</summary>
    public void OnInputEdited(string text)
    {
        if (!_isVoiceDraft || text == _draftText)
            return;
        _isVoiceDraft = false;
        _draftText = null;
        OnPropertyChanged(nameof(IsVoiceDraft));
    }

    // ---- 內部 ----

    private void OnVoicePropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var reason = _shown ? _voice.DisabledReasonFor(VoiceOrigin.Sprite) : Strings.Sprite_VoiceHidden;
        MicEnabled = reason is null;
        MicTip = reason ?? Strings.Composer_Mic;
        StartVoiceCommand.NotifyCanExecuteChanged();

        OnPropertyChanged(nameof(IsVoiceActive));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsTranscribing));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(RecordingName));
        OnPropertyChanged(nameof(FailureText));
        OnPropertyChanged(nameof(Level));
    }

    private static string FormatClock(TimeSpan time) => $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
}
