using CommunityToolkit.Mvvm.Input;
using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>
/// 小精靈專屬對話（M22）在工作台這一側：開著它時，「⋯」的「刪除」換成「清除內容」
/// （確認後刪掉全部訊息與整理紀錄，保留對話與 id）。切換對話的規則在 <c>SpriteConversationSwitcher</c>。
/// </summary>
public partial class WorkbenchViewModel
{
    private readonly SpriteConversationService? _spriteConversation;

    /// <summary>目前開著的是小精靈的專屬對話。</summary>
    public bool IsSpriteConversation => _spriteConversation?.IsSpriteConversation(CurrentConversationId) == true;

    /// <summary>「⋯」是否顯示「刪除」（小精靈的專屬對話沒有刪除）。</summary>
    public bool ShowDeleteConversation => !IsSpriteConversation;

    /// <summary>空對話（沒有任何訊息）或回應中不能清除。</summary>
    public bool ClearConversationEnabled =>
        IsSpriteConversation && !IsResponding && Messages.Count > 0 && History.CanClearConversation;

    private void NotifySpriteConversation()
    {
        OnPropertyChanged(nameof(IsSpriteConversation));
        OnPropertyChanged(nameof(ShowDeleteConversation));
        NotifyConversationActions();
    }

    private bool CanClearConversation() => ClearConversationEnabled;

    /// <summary>確認後的流程（資料庫、通知重新載入）在 <see cref="HistoryViewModel.ClearConversationAsync"/>，和清單的「清除內容」相同。</summary>
    [RelayCommand(CanExecute = nameof(CanClearConversation))]
    private Task ClearConversationAsync() =>
        CurrentConversationId is { } id ? History.ClearConversationAsync(id) : Task.CompletedTask;

    /// <summary>小精靈的對話被清除了：開著它就重新載入（變成空白），並更新清單。</summary>
    async Task IHistoryHost.ConversationClearedAsync(string conversationId)
    {
        if (conversationId != CurrentConversationId)
            return;
        ErrorMessage = null;
        await ReloadMessagesAsync();
        NotifySpriteConversation();
        await History.RefreshAsync(CurrentConversationId);
    }
}
