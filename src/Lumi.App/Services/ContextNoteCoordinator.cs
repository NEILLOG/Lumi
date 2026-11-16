using Lumi.App.ViewModels;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Services;

/// <summary>
/// 研究本與聊天會話的接線（M35 包 35J）：把會話的事件轉給研究本服務與視窗。
/// <list type="bullet">
/// <item>一輪回應成功結束 → <see cref="IContextNoteService.NotifyTurnFinished"/>（失敗、停止、未預期的錯誤都不通知）。</item>
/// <item>工作台換了對話 → <see cref="IContextNoteWindowService.FollowConversationAsync"/>（視窗跟著換；支線裡跟著主線，M36 E5）。</item>
/// <item>切到小精靈模式 → <see cref="IContextNoteWindowService.CloseAsync"/>。</item>
/// </list>
/// 排程執行不經過 <see cref="IChatSession"/>，不用處理。
/// </summary>
public sealed class ContextNoteCoordinator : IDisposable
{
    private readonly IChatSession _session;
    private readonly IContextNoteService _notes;
    private readonly IContextNoteWindowService _window;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<ContextNoteCoordinator> _logger;
    private readonly IBranchStore? _branches;

    /// <param name="branches">支線的資料（M36）：支線裡視窗跟著主線；沒有就一律跟著對話本身。</param>
    public ContextNoteCoordinator(IChatSession session, IContextNoteService notes, IContextNoteWindowService window, IUiDispatcher dispatcher,
        ILogger<ContextNoteCoordinator>? logger = null, IBranchStore? branches = null)
    {
        _session = session;
        _notes = notes;
        _window = window;
        _dispatcher = dispatcher;
        _logger = logger ?? NullLogger<ContextNoteCoordinator>.Instance;
        _branches = branches;

        _session.RunFinished += OnRunFinished;
        _session.ConversationChanged += OnConversationChanged;
        _session.ModeChanged += OnModeChanged;
    }

    private void OnRunFinished(object? sender, RunFinishedArgs args)
    {
        var result = args.Result;
        // 只有真的完成的一輪才算一輪：失敗、被停止、未預期的錯誤都不通知
        if (result.Failure is not null || result.Cancelled || result.Unexpected)
            return;
        _notes.NotifyTurnFinished(result.ConversationId);
    }

    private void OnConversationChanged(object? sender, ConversationChangedArgs args)
    {
        var id = args.Conversation.Id;
        _dispatcher.Post(() => _ = FollowAsync(id));
    }

    private void OnModeChanged(object? sender, ChatMode mode)
    {
        if (mode == ChatMode.Sprite)
            _dispatcher.Post(() => _ = CloseAsync());
    }

    private async Task FollowAsync(string conversationId)
    {
        try
        {
            // 研究本跟著主線：支線的根就是它的主線（沒有支線資料時是對話本身）
            var root = _branches is null ? null : await _branches.GetAsync(conversationId);
            await _window.FollowConversationAsync(root?.RootId ?? conversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "研究本視窗跟著對話切換時失敗");
        }
    }

    private async Task CloseAsync()
    {
        try
        {
            await _window.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "切到小精靈時關閉研究本視窗失敗");
        }
    }

    public void Dispose()
    {
        _session.RunFinished -= OnRunFinished;
        _session.ConversationChanged -= OnConversationChanged;
        _session.ModeChanged -= OnModeChanged;
    }
}
