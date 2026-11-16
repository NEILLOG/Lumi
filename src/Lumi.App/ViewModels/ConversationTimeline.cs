using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>
/// 把存檔的訊息轉成訊息列表（重開程式、切換對話、重讀時用）：使用者氣泡、助理氣泡（有文字才有）、
/// 工具段落（連續的工具呼叫，結果從後面的 <c>Tool</c> 訊息配對回來）。串流時的組成方式要和這裡一致。
/// 上下文整理（M22）：摘要涵蓋到的最後一則（<see cref="CompactionState.SummaryUptoSeq"/>）之後插一條分隔線。
/// 支線（M36）：出處卡片放在最上面；摘要卡放在交出它的那則使用者訊息上方，還沒交出的放在最後（依結束時間）。
/// 交出的訊息已經不在對話裡（被截掉）時，摘要卡不顯示（支線本身還在歷史對話裡）。
/// </summary>
internal static class ConversationTimeline
{
    /// <param name="branches">支線的出處卡片與摘要卡（M36）；null 時輸出和沒有這個參數時完全相同。</param>
    /// <param name="time">判斷摘要卡是不是剛帶回（要播進入動畫）；null 用系統時間。</param>
    public static List<MessageViewModel> Build(IReadOnlyList<ChatMessage> history, Func<ToolUseBlock, ToolCardViewModel> createCard, CompactionState? compaction = null, bool markUnfinished = true,
        BranchTimelineInfo? branches = null, TimeProvider? time = null)
    {
        var items = new List<MessageViewModel>();
        var cards = new Dictionary<string, ToolCardViewModel>(StringComparer.Ordinal);
        BranchCards(branches, time ?? TimeProvider.System, out var delivered, out var pending);

        if (branches?.Origin is { } origin)
            items.Add(new BranchOriginViewModel(origin));

        foreach (var message in history)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    if (delivered.TryGetValue(message.Id, out var before))
                        items.AddRange(before);
                    items.Add(MessageViewModel.From(message));
                    break;

                case ChatRole.Assistant:
                {
                    var uses = message.Content.OfType<ToolUseBlock>().ToList();
                    var hasText = message.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0);
                    var hasThinking = message.Content.OfType<ThinkingBlock>().Any(t => t.Thinking.Length > 0);
                    // 只有思考或工具呼叫、沒有文字的中間步驟不顯示（Q5）。
                    // 工具之後完全空白的回答也不顯示：附件會自成一則（M25，ui-spec 3.1），與串流時拿掉空氣泡一致
                    var emptyAfterTools = !hasText && !hasThinking && items.LastOrDefault() is ToolRunViewModel previousRun
                        && previousRun.Cards.Any(c => c.ReplyAttachments.Count > 0);
                    if ((hasText || uses.Count == 0) && !emptyAfterTools)
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

            if (compaction?.SummaryUptoSeq == message.Seq && compaction.Summary is { Length: > 0 } summary)
                items.Add(new CompactionDividerViewModel(summary));
        }

        items.AddRange(pending);

        // 沒有結果的呼叫（程式在中途結束）：標示成沒有結果，不要永遠轉圈。
        // 進行中的排程執行（M24）例外：還沒有結果是正常的，後續事件會補上
        if (markUnfinished)
        {
            foreach (var card in cards.Values.Where(c => c.IsRunning))
                card.MarkNoResult();
        }

        return items;
    }

    /// <summary>
    /// 支線的摘要卡分成兩組：交出它的使用者訊息 id（放在那則上方）、還沒交出的（放在最後）。
    /// 交出的卡依結束時間排；同一則使用者訊息交出多張時也依結束時間。
    /// </summary>
    private static void BranchCards(BranchTimelineInfo? branches, TimeProvider time,
        out Dictionary<string, List<MessageViewModel>> delivered, out List<MessageViewModel> pending)
    {
        delivered = new Dictionary<string, List<MessageViewModel>>(StringComparer.Ordinal);
        pending = [];
        if (branches is null)
            return;

        foreach (var info in branches.Cards.OrderBy(c => c.EndedAt))
        {
            var card = new BranchSummaryCardViewModel(info, time);
            if (info.DeliveredMessageId is null)
            {
                pending.Add(card);
                continue;
            }
            if (!delivered.TryGetValue(info.DeliveredMessageId, out var list))
                delivered[info.DeliveredMessageId] = list = [];
            list.Add(card);
        }
    }
}
