using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.App.Tests;

/// <summary>M36 36G：訊息列表的支線項目（出處卡片、摘要卡）的位置、摘要卡的文字，以及沒有支線時輸出不變。</summary>
public sealed class BranchTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly FakeTimeProvider Now = new();   // 2026-10-19 12:00 UTC

    private static ChatMessage User(string id, string text, DateTimeOffset at) =>
        new(id, ChatRole.User, [new Lumi.Core.Chat.TextBlock(text)], at);

    private static ChatMessage Assistant(string id, string text, DateTimeOffset at) =>
        new(id, ChatRole.Assistant, [new Lumi.Core.Chat.TextBlock(text)], at);

    private static List<ChatMessage> Turns() =>
    [
        User("u1", "問一", T0),
        Assistant("a1", "答一", T0.AddMinutes(1)),
        User("u2", "問二", T0.AddMinutes(2)),
        Assistant("a2", "答二", T0.AddMinutes(3)),
    ];

    private static List<MessageViewModel> Build(IReadOnlyList<ChatMessage> history, BranchTimelineInfo? branches = null) =>
        ConversationTimeline.Build(history, _ => throw new InvalidOperationException("沒有工具"), branches: branches, time: Now);

    private static BranchCardInfo Card(string id, string? deliveredTo, DateTimeOffset endedAt, int messages = 3, int children = 0) =>
        new("b-" + id, "標題" + id, "摘要" + id, T0, endedAt, T0.AddMinutes(1), messages, children, deliveredTo);

    [Fact]
    public void Origin_is_first()
    {
        var origin = new BranchOriginInfo(null, T0.AddMinutes(1), "答一的開頭", null);

        var items = Build(Turns(), new BranchTimelineInfo(origin, []));

        var first = Assert.IsType<BranchOriginViewModel>(items[0]);
        Assert.Same(origin, first.Info);
        Assert.Equal(5, items.Count);
    }

    [Fact]
    public void Delivered_card_is_before_delivering_user_message()
    {
        var branches = new BranchTimelineInfo(null, [Card("x", "u2", T0.AddMinutes(10))]);

        var items = Build(Turns(), branches);

        var card = Assert.IsType<BranchSummaryCardViewModel>(Assert.Single(items.OfType<BranchSummaryCardViewModel>()));
        Assert.Equal("b-x", card.ConversationId);
        var user2 = items.Single(i => i.IsUser && i.Text == "問二");
        Assert.Same(card, items[items.IndexOf(user2) - 1]);
    }

    [Fact]
    public void Card_delivered_to_a_message_that_is_gone_is_not_shown()
    {
        var branches = new BranchTimelineInfo(null, [Card("gone", "不存在的訊息", T0.AddMinutes(10))]);

        var items = Build(Turns(), branches);

        Assert.DoesNotContain(items, i => i is BranchSummaryCardViewModel);
        Assert.Equal(4, items.Count);
    }

    [Fact]
    public void Pending_cards_are_last_ordered_by_end()
    {
        var late = Card("late", null, T0.AddMinutes(30));
        var early = Card("early", null, T0.AddMinutes(20));
        var branches = new BranchTimelineInfo(null, [late, early]);

        var items = Build(Turns(), branches);

        var tail = items.TakeLast(2).Cast<BranchSummaryCardViewModel>().ToList();
        Assert.Equal(["b-early", "b-late"], tail.Select(c => c.ConversationId));
        Assert.True(tail.All(c => c.IsPending));
        Assert.Same(items[^1], tail[1]);
    }

    [Fact]
    public void Null_branches_output_unchanged()
    {
        var withNull = Build(Turns(), null);
        var omitted = ConversationTimeline.Build(Turns(), _ => throw new InvalidOperationException("沒有工具"));
        var none = Build(Turns(), BranchTimelineInfo.None);

        static string[] Describe(IEnumerable<MessageViewModel> items) =>
            items.Select(i => $"{i.GetType().Name}|{i.IsUser}|{i.Text}|{i.IsMarker}").ToArray();
        Assert.Equal(Describe(omitted), Describe(withNull));
        Assert.Equal(Describe(omitted), Describe(none));
    }

    [Fact]
    public void Branch_blocks_do_not_produce_chips()
    {
        var history = new List<ChatMessage>
        {
            new("u1", ChatRole.User,
                [new Lumi.Core.Chat.TextBlock("問"),
                 new ContextBlock(ContextKind.BranchOrigin, "branch_origin", "a0", "<branch_start>…</branch_start>", true),
                 new ContextBlock(ContextKind.BranchSummary, "標題", "b1", "摘要", true)],
                T0),
        };

        var user = Assert.Single(Build(history));

        Assert.Empty(user.Attachments);
    }

    [Fact]
    public void Origin_quote_split_and_fallback()
    {
        var found = new BranchOriginViewModel(new BranchOriginInfo(null, T0, "甲乙丙丁戊己", "丙丁"));
        Assert.Equal("…甲乙", found.ExcerptBefore);
        Assert.Equal("丙丁", found.ExcerptQuote);
        Assert.Equal("戊己…", found.ExcerptAfter);
        Assert.True(found.HasHighlight);
        Assert.Null(found.ExtraQuoteText);

        var missing = new BranchOriginViewModel(new BranchOriginInfo(null, T0, "甲乙丙丁戊己", "不在這裡"));
        Assert.Equal("…甲乙丙丁戊己…", missing.ExcerptBefore);
        Assert.Equal("", missing.ExcerptQuote);
        Assert.False(missing.HasHighlight);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_OriginQuoteFormat, "不在這裡"), missing.ExtraQuoteText);

        var none = new BranchOriginViewModel(new BranchOriginInfo(null, T0, "甲乙", null));
        Assert.Null(none.ExtraQuoteText);
        Assert.False(none.HasHighlight);
    }

    [Fact]
    public void Origin_eyebrow_names_the_parent()
    {
        var time = T0.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);

        var fromMain = new BranchOriginViewModel(new BranchOriginInfo(null, T0, "開頭", null));
        var fromBranch = new BranchOriginViewModel(new BranchOriginInfo("第一層", T0, "開頭", null));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_OriginFromMainFormat, time), fromMain.Eyebrow);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_OriginFromBranchFormat, "第一層", time), fromBranch.Eyebrow);
    }

    [Fact]
    public void Card_meta_formats()
    {
        var sameDay = new BranchSummaryCardViewModel(Card("s", "u1", T0.AddMinutes(90), messages: 4), Now);
        var withChildren = new BranchSummaryCardViewModel(Card("c", "u1", T0.AddMinutes(90), messages: 4, children: 2), Now);
        var nextDay = new BranchSummaryCardViewModel(Card("n", null, T0.AddHours(30), messages: 2), Now);

        var start = T0.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        var endSameDay = T0.AddMinutes(90).ToLocalTime();
        var fork = T0.AddMinutes(1).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_CardMetaFormat, start,
            endSameDay.ToString("HH:mm", CultureInfo.InvariantCulture), 4, fork), sameDay.MetaText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_CardMetaWithChildrenFormat, start,
            endSameDay.ToString("HH:mm", CultureInfo.InvariantCulture), 4, 2, fork), withChildren.MetaText);

        // 跨日：結束時間也寫日期
        var endNextDay = T0.AddHours(30).ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_CardMetaFormat, start, endNextDay, 2, fork), nextDay.MetaText);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_CardTitleFormat, "標題s"), sameDay.Title);
        Assert.Equal("摘要s", sameDay.Summary);
        Assert.Equal(Strings.Branch_CardCollapse, sameDay.ToggleTip);
    }

    [Fact]
    public void Card_plays_enter_animation_only_when_just_merged_and_pending()
    {
        var justMerged = new BranchSummaryCardViewModel(Card("j", null, Now.GetUtcNow().AddSeconds(-2)), Now);
        var old = new BranchSummaryCardViewModel(Card("o", null, Now.GetUtcNow().AddHours(-1)), Now);
        var delivered = new BranchSummaryCardViewModel(Card("d", "u1", Now.GetUtcNow().AddSeconds(-2)), Now);

        Assert.True(justMerged.PlayEnterAnimation);
        Assert.False(old.PlayEnterAnimation);
        Assert.False(delivered.PlayEnterAnimation);
    }
}
