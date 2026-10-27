using Lumi.App.Sprites;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>協調者操作的視窗。實作在 Views（開真的視窗），測試用假的。</summary>
public interface IWindowHost
{
    bool IsWorkbenchVisible { get; }

    bool IsSpriteVisible { get; }

    void ShowWorkbench();

    void HideWorkbench();

    void ShowSprite();

    void HideSprite();

    /// <summary>工作台捲到某張工具卡片（「在工作台檢視」、「開啟完整內容」）。</summary>
    void ScrollWorkbenchToCall(string callId);

    void Exit();
}

public interface IAppModeCoordinator
{
    ChatMode ActiveMode { get; }

    /// <summary>小精靈被「暫時隱藏」中。</summary>
    bool IsSpriteTemporarilyHidden { get; }

    /// <summary>啟動：回到上次的模式（第一次是工作台模式）。</summary>
    void Start();

    /// <summary>工作台按「小精靈模式」：隱藏工作台、顯示小精靈。回應中不能切換，回 false。</summary>
    bool EnterSpriteMode();

    /// <summary>
    /// 小精靈的右鍵選單、氣泡連結、托盤的「開啟工作台」：隱藏小精靈、切換到工作台模式（兩個視窗不同時存在）。
    /// 回應中不能切換，工作台先以「查看中」開啟（唯讀），回應結束再切換成工作台模式。
    /// </summary>
    void OpenWorkbench(string? scrollToCallId = null);

    /// <summary>
    /// 小精靈的氣泡要在工作台看「小精靈那則對話」（完整內容、確認、錯誤的「在工作台開啟」，M22）：
    /// 和 <see cref="OpenWorkbench"/> 一樣，但工作台顯示小精靈的專屬對話，不切回原本工作台的對話。預設等同 <see cref="OpenWorkbench"/>。
    /// </summary>
    void OpenWorkbenchForSpriteConversation(string? scrollToCallId = null) => OpenWorkbench(scrollToCallId);

    /// <summary>工作台要送出訊息之前：在工作台送出就等於切換到工作台模式（小精靈隱藏）。</summary>
    void WorkbenchWillSend();

    /// <summary>
    /// 工作台視窗要關閉：工作台模式下＝結束程式（回 true，視窗照常關）；小精靈模式下（查看中）關掉工作台、回到小精靈（回 false，由視窗改成隱藏）。
    /// </summary>
    bool WorkbenchMayClose();

    /// <summary>托盤的「顯示小精靈」：提前結束暫時隱藏；在工作台模式下等於切換到小精靈模式。</summary>
    void ShowSprite();

    /// <summary>右鍵選單的「暫時隱藏 30 分鐘」；時間到自動顯示。</summary>
    void HideSpriteTemporarily();

    void Exit();
}

/// <summary>
/// 把「哪個視窗顯示」的規則集中在一處（M7 的 Q1）：<b>兩種模式互斥，兩個視窗也不同時存在</b>——工作台模式時小精靈隱藏；
/// 小精靈模式時工作台隱藏，從小精靈打開工作台就切換到工作台模式；回應中不能切換模式。
/// 「查看中」不是這裡的狀態：它就是「回應中且作用中的模式是小精靈」，由工作台自己推導；回應結束時這裡把模式切換過去。
/// </summary>
public sealed class AppModeCoordinator : IAppModeCoordinator
{
    private readonly IChatSession _session;
    private readonly IWindowHost _windows;
    private readonly IModelCatalog _settings;
    private readonly SpriteShellOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AppModeCoordinator> _logger;
    private readonly SpriteConversationSwitcher? _conversations;
    private CancellationTokenSource? _hideTimer;
    private bool _switchToWorkbenchWhenIdle;   // 回應中從小精靈打開工作台（查看中）：回應結束再切換

    public AppModeCoordinator(
        IChatSession session, IWindowHost windows, IModelCatalog settings, SpriteShellOptions options, TimeProvider time,
        ILogger<AppModeCoordinator> logger, SpriteConversationSwitcher? conversations = null)
    {
        _session = session;
        _windows = windows;
        _settings = settings;
        _options = options;
        _time = time;
        _logger = logger;
        _conversations = conversations;
        _session.ModeChanged += (_, mode) => _ = SaveModeAsync(mode);
        _session.EventReceived += OnEvent;
        _session.RunFinished += OnRunFinished;
    }

    public ChatMode ActiveMode => _session.ActiveMode;

    public bool IsSpriteTemporarilyHidden { get; private set; }

    public void Start()
    {
        if (_settings.Current.Sprite.LastMode == ChatMode.Sprite && _session.TrySetMode(ChatMode.Sprite))
            _windows.ShowSprite();
        else
            _windows.ShowWorkbench();
    }

    public bool EnterSpriteMode()
    {
        if (!_session.TrySetMode(ChatMode.Sprite))
            return false;

        CancelTemporaryHide();
        _windows.HideWorkbench();
        _windows.ShowSprite();
        return true;
    }

    public void OpenWorkbench(string? scrollToCallId = null)
    {
        if (_session.ActiveMode == ChatMode.Sprite)
        {
            _switchToWorkbenchWhenIdle = !_session.TrySetMode(ChatMode.Workbench);
            CancelTemporaryHide();
            _windows.HideSprite();
        }
        _windows.ShowWorkbench();
        if (scrollToCallId is not null)
            _windows.ScrollWorkbenchToCall(scrollToCallId);
    }

    public void OpenWorkbenchForSpriteConversation(string? scrollToCallId = null)
    {
        if (_session.ActiveMode == ChatMode.Sprite)
            _conversations?.KeepSpriteConversationOnNextWorkbench();   // 切到工作台模式時不換掉對話
        OpenWorkbench(scrollToCallId);
    }

    public void WorkbenchWillSend()
    {
        if (_session.ActiveMode == ChatMode.Workbench || !_session.TrySetMode(ChatMode.Workbench))
            return;

        CancelTemporaryHide();
        _windows.HideSprite();
    }

    public bool WorkbenchMayClose()
    {
        if (_session.ActiveMode == ChatMode.Workbench)
        {
            _windows.Exit();
            return true;
        }
        // 只有查看中才會在小精靈模式下看到工作台：關掉就回到小精靈，回應結束後也不再切換
        _switchToWorkbenchWhenIdle = false;
        _conversations?.KeepSpriteConversationOnNextWorkbench(false);
        _windows.ShowSprite();
        return false;
    }

    public void ShowSprite()
    {
        if (_session.ActiveMode == ChatMode.Sprite)
        {
            _switchToWorkbenchWhenIdle = false;
            _conversations?.KeepSpriteConversationOnNextWorkbench(false);
            CancelTemporaryHide();
            _windows.HideWorkbench();
            _windows.ShowSprite();
        }
        else
        {
            EnterSpriteMode();
        }
    }

    public void HideSpriteTemporarily()
    {
        CancelTemporaryHide();
        _windows.HideSprite();
        IsSpriteTemporarilyHidden = true;

        var timer = _hideTimer = new CancellationTokenSource();
        _ = ShowAfterAsync(_options.TemporaryHide, timer.Token);
    }

    public void Exit() => _windows.Exit();

    private async Task ShowAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        IsSpriteTemporarilyHidden = false;
        if (_session.ActiveMode == ChatMode.Sprite)
            _windows.ShowSprite();
    }

    private void CancelTemporaryHide()
    {
        _hideTimer?.Cancel();
        _hideTimer = null;
        IsSpriteTemporarilyHidden = false;
    }

    /// <summary>小精靈被隱藏時需要確認：自動顯示，不然對話迴圈會一直等。</summary>
    private void OnEvent(object? sender, ChatEvent chatEvent)
    {
        if (chatEvent is ApprovalRequired && IsSpriteTemporarilyHidden && _session.ActiveMode == ChatMode.Sprite)
        {
            CancelTemporaryHide();
            _windows.ShowSprite();
        }
    }

    private void OnRunFinished(object? sender, RunFinishedArgs args)
    {
        if (!_switchToWorkbenchWhenIdle)
            return;
        _switchToWorkbenchWhenIdle = false;
        if (_windows.IsWorkbenchVisible)
            _session.TrySetMode(ChatMode.Workbench);
    }

    private async Task SaveModeAsync(ChatMode mode)
    {
        try
        {
            if (_settings.Current.Sprite.LastMode != mode)
                await _settings.SetSpriteAsync(_settings.Current.Sprite with { LastMode = mode });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "儲存上次的模式失敗");
        }
    }
}
