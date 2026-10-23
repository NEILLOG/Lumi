using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>
/// 歷史抽屜需要工作台做的事：切換對話、目前對話被刪除時導向、標題變了時同步視窗標題。由 <see cref="WorkbenchViewModel"/> 實作。
/// </summary>
public interface IHistoryHost
{
    /// <summary>目前開著的對話；沒有時 null。</summary>
    string? CurrentConversationId { get; }

    /// <summary>回應中（含小精靈回應中、等待工具確認）。回應中的對話不能刪除（見 <see cref="HistoryViewModel"/>）。</summary>
    bool IsResponding { get; }

    /// <summary>切換到清單中的某則對話（回應中會先停止）。</summary>
    Task OpenAsync(Conversation conversation);

    /// <summary>
    /// 對話已經從資料庫刪除。若它是目前開著的對話：切到 <paramref name="next"/>（null＝開新的空白對話），並清掉輸入框草稿與上下文列。
    /// </summary>
    Task ConversationDeletedAsync(string conversationId, Conversation? next);

    /// <summary>對話改名了（只通知；資料庫已經寫好）。</summary>
    void TitleChanged(string conversationId, string title);
}
