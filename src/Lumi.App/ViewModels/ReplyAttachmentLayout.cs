namespace Lumi.App.ViewModels;

/// <summary>
/// 回覆附件的歸屬（M25，ui-spec 3.1、計畫 R1）：工具段落產生的附件，分配到「工具之後的第一則助理文字」；
/// 中間沒有文字（後面是使用者訊息、另一個工具段落、或結尾）時，補一則只放附件的訊息（<see cref="MessageViewModel.AttachmentHost"/>），緊接在工具段落後面。
/// 純邏輯：只依訊息列表的順序與型別決定，不碰畫面。工作台、排程紀錄檢視共用。
/// 附件不存檔，從工具結果還原（<see cref="ToolCardViewModel.ReplyAttachments"/>），所以重開對話與重讀都跑同一套分配。
/// </summary>
internal static class ReplyAttachmentLayout
{
    /// <summary>停止時工具結果的摘要（<c>ConversationService</c> 寫入的字串，和 Core 相同）。</summary>
    private const string StoppedSummary = "已停止";

    private static readonly IReadOnlyList<ReplyAttachmentViewModel> NoAttachments = [];

    /// <summary>一個工具段落的計算結果：附件、接住附件的真的助理訊息（沒有就是 null）、既有的補充訊息（可以沿用）。</summary>
    private sealed record Plan(ToolRunViewModel Run, List<ReplyAttachmentViewModel> Attachments, MessageViewModel? Target, MessageViewModel? Host);

    /// <summary>
    /// 依目前的訊息順序重新分配所有工具段落的附件，並同步每則訊息的 <see cref="MessageViewModel.ReplyAttachments"/>。
    /// 內容相同的訊息不動（不重建畫面）。不需要的補充訊息會從 <paramref name="items"/> 移除。
    /// </summary>
    public static void Apply(IList<MessageViewModel> items)
    {
        var plans = new List<Plan>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not ToolRunViewModel run)
                continue;
            var attachments = run.Cards.SelectMany(c => c.ReplyAttachments).ToList();
            var (target, host) = FindCarrier(items, i);
            plans.Add(new Plan(run, attachments, target, host));
        }

        // 要沿用的補充訊息：有附件、而且後面沒有真的助理文字的工具段落才需要；其餘的補充訊息移除
        var keep = new HashSet<MessageViewModel>(ReferenceEqualityComparer.Instance);
        foreach (var plan in plans)
        {
            if (NeedsHost(plan) && plan.Host is not null)
                keep.Add(plan.Host);
        }

        var removed = new List<MessageViewModel>();
        foreach (var host in items.Where(m => m.IsAttachmentHost).ToList())
        {
            if (keep.Contains(host))
                continue;
            items.Remove(host);
            removed.Add(host);
        }

        // 每則接附件的訊息 → 它應有的附件；真的助理文字優先，沒有就用（沿用或新建的）補充訊息
        var carriers = new Dictionary<MessageViewModel, List<ReplyAttachmentViewModel>>(ReferenceEqualityComparer.Instance);
        foreach (var plan in plans)
        {
            if (plan.Attachments.Count == 0)
                continue;

            MessageViewModel carrier;
            if (plan.Target is not null)
            {
                carrier = plan.Target;
            }
            else
            {
                // 補充訊息必須緊接在工具段落後面：沿用的若不在那裡就搬過去，沒有就新建
                var index = items.IndexOf(plan.Run);
                if (plan.Host is { } host)
                {
                    if (!(index + 1 < items.Count && ReferenceEquals(items[index + 1], host)))
                    {
                        items.Remove(host);
                        index = items.IndexOf(plan.Run);
                        items.Insert(index + 1, host);
                    }
                    carrier = host;
                }
                else
                {
                    carrier = MessageViewModel.AttachmentHost(plan.Run.CreatedAt);
                    items.Insert(index + 1, carrier);
                }
            }
            carriers[carrier] = plan.Attachments;
        }

        foreach (var item in items)
            Sync(item, carriers.TryGetValue(item, out var wanted) ? wanted : NoAttachments);
        foreach (var item in removed)
            Sync(item, NoAttachments);
    }

    /// <summary>
    /// 只放附件的訊息在停止之後標上「已停止」（ui-spec 3.1 狀態表）：它是最後一則，前面的工具段落裡有「已停止」的卡片。
    /// 這個判斷只看存檔就能得到，所以重讀之後也成立；即時的停止由工作台在 <c>OnRunFinished</c> 另外標。
    /// </summary>
    public static void MarkStopped(IList<MessageViewModel> items)
    {
        if (items.Count < 2)
            return;
        if (items[items.Count - 1] is not { IsAttachmentHost: true } host || items[items.Count - 2] is not ToolRunViewModel run)
            return;
        if (run.Cards.Any(c => c.Summary == StoppedSummary))
            host.IsStopped = true;
    }

    /// <summary>
    /// 新的工具呼叫要加進哪個工具段落：最後一項是工具段落；或最後是只放附件的訊息、前一項是工具段落（不另開段落，ui-spec 3.1）。
    /// 都不是就回 null（呼叫端新建一段）。
    /// </summary>
    public static ToolRunViewModel? OpenRunForNewCall(IList<MessageViewModel> items)
    {
        if (items.Count == 0)
            return null;
        var last = items[items.Count - 1];
        if (last is ToolRunViewModel run)
            return run;
        if (last.IsAttachmentHost && items.Count >= 2)
            return items[items.Count - 2] as ToolRunViewModel;
        return null;
    }

    /// <summary>從工具段落的下一項往後找：跳過分隔線與既有的補充訊息，第一個真的助理訊息就是接住附件的訊息；遇到使用者訊息或另一個工具段落就停。</summary>
    private static (MessageViewModel? Target, MessageViewModel? Host) FindCarrier(IList<MessageViewModel> items, int runIndex)
    {
        MessageViewModel? host = null;
        for (var j = runIndex + 1; j < items.Count; j++)
        {
            var item = items[j];
            if (item is CompactionDividerViewModel)
                continue;
            if (item.IsAttachmentHost)
            {
                host ??= item;
                continue;
            }
            // 工具段落與使用者訊息的 IsAssistant 都是 false，所以這裡只剩真的助理訊息會通過
            return item.IsAssistant ? (item, host) : (null, host);
        }
        return (null, host);
    }

    private static bool NeedsHost(Plan plan) => plan.Attachments.Count > 0 && plan.Target is null;

    private static void Sync(MessageViewModel message, IReadOnlyList<ReplyAttachmentViewModel> wanted)
    {
        var current = message.ReplyAttachments;
        if (current.Count == wanted.Count)
        {
            var same = true;
            for (var k = 0; k < current.Count; k++)
            {
                if (!ReferenceEquals(current[k], wanted[k]))
                {
                    same = false;
                    break;
                }
            }
            if (same)
                return;
        }

        current.Clear();
        foreach (var attachment in wanted)
            current.Add(attachment);
    }
}
