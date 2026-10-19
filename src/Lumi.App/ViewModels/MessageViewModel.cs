using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>訊息列表裡的一則訊息。串流中的助理訊息會持續更新 <see cref="Text"/>、<see cref="Thinking"/>。</summary>
public partial class MessageViewModel : ObservableObject
{
    private DateTimeOffset? _thinkingStartedAt;

    private protected MessageViewModel(bool isUser)
    {
        IsUser = isUser;
    }

    public bool IsUser { get; }

    /// <summary>助理的文字氣泡（工具段落 <see cref="ToolRunViewModel"/> 不是）。</summary>
    public virtual bool IsAssistant => !IsUser;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(ShowTypingIndicator), nameof(ShowActions))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThinking))]
    private string _thinking = "";

    [ObservableProperty]
    private bool _isStopped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTypingIndicator), nameof(ShowActions))]
    private bool _isStreaming;

    /// <summary>後面緊接著工具段落：這則只是「先說一聲再去用工具」，不是這一輪的結尾，不顯示動作列。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isFollowedByTools;

    /// <summary>這則的用量；串流中沒有，完成後才有（OpenAI 相容伺服器可能沒回）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageText), nameof(HasUsage))]
    private TokenUsage? _usage;

    /// <summary>目前的對話裡最後一則助理訊息：只有它有「重新產生」。</summary>
    [ObservableProperty]
    private bool _isLastAssistant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    private bool _isCopied;

    public string CopyLabel => IsCopied ? Strings.Workbench_Copied : Strings.Workbench_Copy;

    /// <summary>
    /// 思考花了幾秒。只在當下串流時量得到（第一個思考片段到第一個文字片段或完成）；
    /// 從資料庫載入的訊息是 null，標題就只顯示「思考過程」。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThinkingTitle))]
    private int? _thinkingSeconds;

    public bool HasText => Text.Length > 0;

    public bool HasThinking => Thinking.Length > 0;

    public bool HasUsage => Usage is not null;

    /// <summary>已送出、但還沒收到任何文字時顯示「…」。</summary>
    public bool ShowTypingIndicator => IsStreaming && !HasText;

    /// <summary>動作列（複製、重新產生、token）：助理訊息、不在串流中、有文字才顯示。</summary>
    public bool ShowActions => IsAssistant && !IsStreaming && HasText && !IsFollowedByTools;

    public string ThinkingTitle => ThinkingSeconds is { } seconds
        ? string.Format(CultureInfo.CurrentCulture, Strings.Workbench_ThinkingWithSeconds, seconds)
        : Strings.Workbench_Thinking;

    /// <summary><c>↑ 輸入 1.2K · ↓ 輸出 350 tokens</c>；輸入含快取。</summary>
    public string UsageText => Usage is { } u
        ? string.Format(CultureInfo.CurrentCulture, Strings.Workbench_UsageFormat,
            WorkbenchViewModel.FormatTokens(u.InputTokens + u.CacheCreationInputTokens + u.CacheReadInputTokens),
            WorkbenchViewModel.FormatTokens(u.OutputTokens))
        : "";

    public static MessageViewModel User(string text) => new(isUser: true) { Text = text };

    public static MessageViewModel StreamingAssistant() => new(isUser: false) { IsStreaming = true };

    public static MessageViewModel From(ChatMessage message)
    {
        var vm = new MessageViewModel(message.Role == ChatRole.User);
        vm.ApplyContent(message);
        vm.IsStopped = message.Status == MessageStatus.Stopped;
        return vm;
    }

    public void AppendText(string text, DateTimeOffset now)
    {
        EndThinking(now);
        Text += text;
    }

    public void AppendThinking(string text, DateTimeOffset now)
    {
        _thinkingStartedAt ??= now;
        Thinking += text;
    }

    /// <summary>用完整的訊息取代串流中累積的內容（兩者應該相同，以完整訊息為準）。</summary>
    public void Complete(ChatMessage message, DateTimeOffset now)
    {
        EndThinking(now);
        ApplyContent(message);
        IsStreaming = false;
    }

    public void MarkStopped(DateTimeOffset now)
    {
        EndThinking(now);
        IsStopped = true;
        IsStreaming = false;
    }

    private void EndThinking(DateTimeOffset now)
    {
        if (_thinkingStartedAt is not { } started)
            return;
        _thinkingStartedAt = null;
        ThinkingSeconds = Math.Max(1, (int)Math.Ceiling((now - started).TotalSeconds));
    }

    private void ApplyContent(ChatMessage message)
    {
        Text = string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text));
        Thinking = string.Join("\n\n", message.Content.OfType<ThinkingBlock>().Select(b => b.Thinking).Where(t => t.Length > 0));
        Usage = message.Usage;
    }
}
