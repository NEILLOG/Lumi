using Avalonia.Interactivity;
using Lumi.App.Markdown;
using Lumi.App.ViewModels;

namespace Lumi.App.Controls;

/// <summary>「問這段」的事件參數：右鍵選單的「問這段」送出時帶著選取的文字（還沒正規化，由訊息區處理）。</summary>
public sealed class AskSelectionEventArgs : RoutedEventArgs
{
    public AskSelectionEventArgs(RoutedEvent routedEvent, string text) : base(routedEvent)
    {
        Text = text;
    }

    public string Text { get; }
}

/// <summary>支線的文字選取相關事件與判斷（M36 E8）。</summary>
public static class BranchSelectionEvents
{
    /// <summary>右鍵選單的「問這段」：冒泡到訊息區，由 <c>ConversationView</c> 開支線。</summary>
    public static readonly RoutedEvent<AskSelectionEventArgs> AskRequestedEvent =
        RoutedEvent.Register<EmojiTextBlock, AskSelectionEventArgs>("AskRequested", RoutingStrategies.Bubble);

    /// <summary>
    /// 這段文字能不能「問這段」：所在的 <see cref="MarkdownView"/> 是可以開支線的助理回答（<c>ShowBranchAction</c> 且 <c>CanBranch</c>）。
    /// 回傳那則訊息；不能問就是 null（使用者訊息、摘要卡、第二層、回應中都不行）。
    /// </summary>
    public static MessageViewModel? AskableMessage(MarkdownView? view) =>
        view?.DataContext is MessageViewModel { IsAssistant: true, ShowBranchAction: true, CanBranch: true } message ? message : null;
}
