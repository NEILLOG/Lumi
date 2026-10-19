using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>
/// 把存檔的訊息轉成訊息列表（重開程式、切換對話、重讀時用）：使用者氣泡、助理氣泡（有文字才有）、
/// 工具段落（連續的工具呼叫，結果從後面的 <c>Tool</c> 訊息配對回來）。串流時的組成方式要和這裡一致。
/// </summary>
internal static class ConversationTimeline
{
    public static List<MessageViewModel> Build(IReadOnlyList<ChatMessage> history, Func<ToolUseBlock, ToolCardViewModel> createCard)
    {
        var items = new List<MessageViewModel>();
        var cards = new Dictionary<string, ToolCardViewModel>(StringComparer.Ordinal);

        foreach (var message in history)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    items.Add(MessageViewModel.From(message));
                    break;

                case ChatRole.Assistant:
                {
                    var uses = message.Content.OfType<ToolUseBlock>().ToList();
                    var hasText = message.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0);
                    // 只有思考或工具呼叫、沒有文字的中間步驟不顯示（Q5）
                    if (hasText || uses.Count == 0)
                        items.Add(MessageViewModel.From(message));

                    foreach (var use in uses)
                    {
                        if (items.Count == 0 || items[^1] is not ToolRunViewModel run)
                        {
                            run = new ToolRunViewModel();
                            items.Add(run);
                        }
                        var card = createCard(use);
                        cards[use.Id] = card;
                        run.Cards.Add(card);
                    }
                    break;
                }

                case ChatRole.Tool:
                    foreach (var result in message.Content.OfType<ToolResultBlock>())
                    {
                        if (cards.TryGetValue(result.ToolUseId, out var card))
                            card.Complete(result);
                    }
                    break;
            }
        }

        // 沒有結果的呼叫（程式在中途結束）：標示成沒有結果，不要永遠轉圈
        foreach (var card in cards.Values.Where(c => c.IsRunning))
            card.MarkNoResult();

        return items;
    }
}
