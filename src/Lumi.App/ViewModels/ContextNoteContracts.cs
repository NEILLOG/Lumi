using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>研究本視窗只有一個，永遠顯示工作台目前那則對話的研究本。實作在 Views（35G），測試用假的。</summary>
public interface IContextNoteWindowService
{
    bool IsOpen { get; }
    /// <summary>開啟或帶到前面。這則對話沒有研究本時不做事。</summary>
    Task ShowAsync(string conversationId);
    /// <summary>工作台換了對話：視窗開著時，有研究本就換過去，沒有就關閉。視窗沒開時不做事。</summary>
    Task FollowConversationAsync(string? conversationId);
    /// <summary>關閉（編輯中先存檔）。</summary>
    Task CloseAsync();
}
// 「並排」不在介面上：是 ContextNoteWindowService 自己的方法，由視窗的按鈕呼叫（35G）。

public sealed record ContextNoteStartRequest(string Goal, ContextNoteStartMode Mode);

/// <summary>啟動對話框（35J）。取消回 null。</summary>
public interface IContextNoteStartPrompt
{
    /// <param name="suggestedGoal">預先填好的目標（建議卡片帶來的），沒有就是空字串。</param>
    /// <param name="messageCount">這則對話目前的訊息數；0 時「從目前的對話整理」停用。</param>
    /// <param name="modelText">目前模型的「供應商 · model ID」；null＝沒有可用的模型。</param>
    Task<ContextNoteStartRequest?> PromptAsync(string suggestedGoal, int messageCount, string? modelText);
}

public enum NoteSuggestionState { Pending, Created, Dismissed }

/// <summary>建議卡片的宿主（工作台實作，35J）。</summary>
public interface INoteSuggestionHost
{
    NoteSuggestionState SuggestionState { get; }
    bool CanCreate { get; }
    event EventHandler? SuggestionStateChanged;
    Task CreateFromSuggestionAsync(string goal);
    Task DismissSuggestionAsync();
    Task OpenNoteAsync();
}

public enum ContextNoteWindowMode { Preview, Edit, History }
