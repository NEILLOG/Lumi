using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>訊息列表裡的一則訊息。串流中的助理訊息會持續更新 <see cref="Text"/>、<see cref="Thinking"/>。</summary>
public partial class MessageViewModel : ObservableObject
{
    private MessageViewModel(bool isUser)
    {
        IsUser = isUser;
    }

    public bool IsUser { get; }

    public bool IsAssistant => !IsUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(ShowTypingIndicator))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThinking))]
    private string _thinking = "";

    [ObservableProperty]
    private bool _isStopped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTypingIndicator))]
    private bool _isStreaming;

    public bool HasText => Text.Length > 0;

    public bool HasThinking => Thinking.Length > 0;

    /// <summary>已送出、但還沒收到任何文字時顯示「…」。</summary>
    public bool ShowTypingIndicator => IsStreaming && !HasText;

    public static MessageViewModel User(string text) => new(isUser: true) { Text = text };

    public static MessageViewModel StreamingAssistant() => new(isUser: false) { IsStreaming = true };

    public static MessageViewModel From(ChatMessage message)
    {
        var vm = new MessageViewModel(message.Role == ChatRole.User);
        vm.ApplyContent(message);
        vm.IsStopped = message.Status == MessageStatus.Stopped;
        return vm;
    }

    public void AppendText(string text) => Text += text;

    public void AppendThinking(string text) => Thinking += text;

    /// <summary>用完整的訊息取代串流中累積的內容（兩者應該相同，以完整訊息為準）。</summary>
    public void Complete(ChatMessage message)
    {
        ApplyContent(message);
        IsStreaming = false;
    }

    public void MarkStopped()
    {
        IsStopped = true;
        IsStreaming = false;
    }

    private void ApplyContent(ChatMessage message)
    {
        Text = string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text));
        Thinking = string.Join("\n\n", message.Content.OfType<ThinkingBlock>().Select(b => b.Thinking).Where(t => t.Length > 0));
    }
}
