using Lumi.App.Resources;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>
/// 小精靈專屬對話的切換規則（M22）：<b>單一 <see cref="IChatSession"/> 不變，切模式時切對話</b>。
/// 進入小精靈模式：記下工作台目前的對話，開小精靈的專屬對話（沒有就建立，見 <see cref="SpriteConversationService"/>）；
/// 回到工作台：重新開回記下的那則（被刪了就照 <see cref="IChatSession.InitializeAsync"/> 的規則）。
/// 切換時還在回應就不停止，等 <see cref="IChatSession.RunFinished"/> 之後才切（正常情況下模式在回應中本來就切不過去，這是保險）。
/// 從小精靈的氣泡「在工作台開啟」（完整內容、確認、錯誤）是要看小精靈那則對話本身：
/// 呼叫 <see cref="KeepSpriteConversationOnNextWorkbench"/>，下一次切到工作台時就不切回去。
/// 所有切換排成一條佇列依序做；<see cref="WhenIdleAsync"/> 等目前排隊的切換做完。
/// </summary>
public sealed class SpriteConversationSwitcher
{
    private readonly IChatSession _session;
    private readonly SpriteConversationService _sprite;
    private readonly IConversationStore _store;
    private readonly ILogger<SpriteConversationSwitcher> _logger;
    private Task _queue = Task.CompletedTask;
    private string? _workbenchConversationId;   // 進入小精靈模式前，工作台開著的對話
    private bool _keepSpriteConversation;
    private bool _waitingForRunToFinish;

    public SpriteConversationSwitcher(
        IChatSession session, SpriteConversationService sprite, IConversationStore store, ILogger<SpriteConversationSwitcher> logger)
    {
        _session = session;
        _sprite = sprite;
        _store = store;
        _logger = logger;
        _session.ModeChanged += OnModeChanged;
        _session.RunFinished += OnRunFinished;
    }

    /// <summary>目前排隊的切換都做完了（沒有排隊就立刻完成）。</summary>
    public Task WhenIdleAsync() => _queue;

    /// <summary>
    /// 下一次切到工作台模式時，工作台顯示的是小精靈的對話（不切回原本的）。
    /// 切換前就取消（例如使用者關掉工作台回到小精靈）傳 false。
    /// </summary>
    public void KeepSpriteConversationOnNextWorkbench(bool keep = true) => _keepSpriteConversation = keep;

    private void OnModeChanged(object? sender, ChatMode mode)
    {
        if (mode == ChatMode.Sprite)
            _keepSpriteConversation = false;
        Enqueue();
    }

    private void OnRunFinished(object? sender, RunFinishedArgs args)
    {
        if (!_waitingForRunToFinish)
            return;
        _waitingForRunToFinish = false;
        Enqueue();
    }

    private void Enqueue()
    {
        if (_session.IsResponding)
        {
            _waitingForRunToFinish = true;   // 不停止回應，等結束之後才切
            return;
        }
        _queue = RunAfterAsync(_queue);
    }

    private async Task RunAfterAsync(Task previous)
    {
        await previous.ConfigureAwait(true);
        try
        {
            await SwitchAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切換小精靈專屬對話失敗");
        }
    }

    /// <summary>依「執行當下」的模式切換（連續切換時只收斂到最後的模式）。</summary>
    private async Task SwitchAsync()
    {
        if (_session.IsResponding)
        {
            _waitingForRunToFinish = true;
            return;
        }

        if (_session.ActiveMode == ChatMode.Sprite)
        {
            var spriteConversation = await _sprite.GetOrCreateAsync(Strings.Sprite_ConversationTitle);
            if (_session.ConversationId == spriteConversation.Id)
                return;
            // 工作台正開著小精靈的對話時不覆蓋：回到工作台仍開原本記下的那則
            _workbenchConversationId = _session.ConversationId;
            await _session.OpenConversationAsync(spriteConversation);
            return;
        }

        if (_keepSpriteConversation)
        {
            _keepSpriteConversation = false;
            return;
        }
        // 工作台模式：只有目前開著的是小精靈的對話才需要換回去（工作台自己開著別的對話就不動）
        if (_session.ConversationId is not { } current || current != await _sprite.GetIdAsync())
            return;

        var remembered = _workbenchConversationId is { } id ? await _store.GetConversationAsync(id) : null;
        if (remembered is not null)
            await _session.OpenConversationAsync(remembered);
        else
            await _session.InitializeAsync(Strings.Workbench_NewConversationTitle);   // 被刪了或沒記到：最近一則（不含小精靈的），沒有就新開
    }
}
