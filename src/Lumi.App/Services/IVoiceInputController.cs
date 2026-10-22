using System.ComponentModel;
using Lumi.App.Resources;

namespace Lumi.App.Services;

/// <summary>語音輸入的狀態（M6 6D）。Failed 會保留錄音，可以重試轉寫或放棄。</summary>
public enum VoiceInputState { Idle, Recording, Transcribing, Failed }

/// <summary>收音結束後怎麼處理：Edit＝文字放進輸入框（標「語音轉寫」）；Send＝直接送出。</summary>
public enum VoiceStopAction { Edit, Send }

/// <summary>是哪一個介面開始的錄音（M6 6E）。工作台與小精靈共用一個控制器，結果只交給開始的那一邊處理。</summary>
public enum VoiceOrigin { Workbench, Sprite }

/// <summary>轉寫完成（且不是空白）。<see cref="ConversationId"/> 是開始錄音時的對話；呼叫端比對後決定送出或退回編輯。</summary>
/// <param name="Language">服務回報的語言，沒有時是 null。</param>
/// <param name="Origin">開始錄音的介面；只有該介面處理這個結果。</param>
public sealed record VoiceTranscribedArgs(string Text, VoiceStopAction Action, string? ConversationId, string? Language, VoiceOrigin Origin);

/// <summary>
/// 語音輸入控制器（工作台與小精靈共用，單例）。只管狀態與錄音、轉寫流程，不碰畫面；
/// 所有事件與屬性變更都經 <see cref="Lumi.App.ViewModels.IUiDispatcher"/> 回到 UI 執行緒。
/// </summary>
public interface IVoiceInputController : INotifyPropertyChanged
{
    VoiceInputState State { get; }

    /// <summary>錄音中的已錄長度（其他狀態是 0）。</summary>
    TimeSpan Elapsed { get; }

    /// <summary>錄音結束後的長度，轉寫中與失敗時顯示用；閒置時是 null。</summary>
    TimeSpan? RecordedDuration { get; }

    /// <summary>音量 0..1（錄音中，節流後更新）。</summary>
    double Level { get; }

    /// <summary>失敗時可直接顯示給使用者的訊息（繁中，不含 Token）。</summary>
    string? LastFailureMessage { get; }

    /// <summary>一次性的提示（太短、沒聽到內容、麥克風權限）；下次開始收音或呼叫 <see cref="ClearNotice"/> 時清除。</summary>
    string? Notice { get; }

    /// <summary>現在能不能開始錄音（端點、Token、回應中、已在錄音或轉寫中）。</summary>
    bool CanStart { get; }

    /// <summary>不能開始時的原因（給 Tooltip）；可以開始時是 null。</summary>
    string? DisabledReason { get; }

    /// <summary>目前這段錄音（或失敗保留的錄音）是哪一邊開始的；從來沒錄過時是 null。</summary>
    VoiceOrigin? Origin { get; }

    /// <summary>
    /// 某一邊的麥克風要不要停用、原因是什麼：錄音或轉寫中而且不是這一邊開始的，原因是「另一個視窗正在語音輸入」；
    /// 其他情況同 <see cref="DisabledReason"/>。
    /// </summary>
    string? DisabledReasonFor(VoiceOrigin origin);

    /// <summary>轉寫完成。在 UI 執行緒上發出，且發出時狀態已經是 Idle。</summary>
    event EventHandler<VoiceTranscribedArgs>? Transcribed;

    /// <summary>不能開始時什麼都不做（呼叫端應先看 <see cref="CanStart"/>）。麥克風開啟失敗時設定 <see cref="Notice"/>。</summary>
    Task StartAsync(VoiceOrigin origin, CancellationToken ct = default);

    /// <summary>停止錄音並轉寫。不在錄音中時什麼都不做；不會丟例外。</summary>
    Task StopAsync(VoiceStopAction action);

    /// <summary>取消：錄音中丟掉錄音；轉寫中取消請求並丟掉錄音；失敗中等同 <see cref="Discard"/>。</summary>
    void Cancel();

    /// <summary>失敗後重試轉寫（用保留的錄音與原本的動作）。</summary>
    Task RetryAsync();

    /// <summary>放棄失敗的錄音，回到閒置。</summary>
    void Discard();

    void ClearNotice();
}

/// <summary>沒有語音功能時的替身（測試或沒有註冊控制器時）：一律不能開始，原因是「尚未設定」。</summary>
internal sealed class NoVoiceInputController : IVoiceInputController
{
    public static readonly NoVoiceInputController Instance = new();

    public VoiceInputState State => VoiceInputState.Idle;
    public TimeSpan Elapsed => TimeSpan.Zero;
    public TimeSpan? RecordedDuration => null;
    public double Level => 0;
    public string? LastFailureMessage => null;
    public string? Notice => null;
    public bool CanStart => false;
    public string? DisabledReason => Strings.Voice_NotConfigured;
    public VoiceOrigin? Origin => null;
    public string? DisabledReasonFor(VoiceOrigin origin) => DisabledReason;

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public event EventHandler<VoiceTranscribedArgs>? Transcribed { add { } remove { } }

    public Task StartAsync(VoiceOrigin origin, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(VoiceStopAction action) => Task.CompletedTask;
    public void Cancel() { }
    public Task RetryAsync() => Task.CompletedTask;
    public void Discard() { }
    public void ClearNotice() { }
}
