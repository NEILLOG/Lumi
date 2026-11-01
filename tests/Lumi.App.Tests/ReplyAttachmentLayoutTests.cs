using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tarot;

namespace Lumi.App.Tests;

/// <summary>M25 25C：回覆附件的歸屬（純邏輯，ui-spec 3.1、計畫 R1）與只放附件的訊息狀態。</summary>
public class ReplyAttachmentLayoutTests
{
    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private const string SpreadJson = """
        {"spread_id":"three_card_timeline","spread_name":"三張牌","allow_reversed":true,"positions":[
          {"index":1,"position_name":"過去","layout":{"x":-1,"y":0,"rotation":0,"z":0},
           "card":{"id":"major_00","name_zh":"愚者","name_en":"The Fool","element":null,"orientation":"upright"}}]}
        """;

    private static ToolCardViewModel Card(string callId, string toolName = "echo") =>
        new(callId, toolName, toolName, ToolPermission.ReadOnly, null, "{}", new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());

    /// <summary>完成的塔羅卡片：結果是牌陣 JSON，會產生一個牌陣附件。</summary>
    private static ToolCardViewModel TarotCard(string callId)
    {
        var card = Card(callId, TarotToolNames.Draw);
        card.Complete(new ToolResultBlock(callId, SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired));
        return card;
    }

    /// <summary>完成的圖片卡片：結果帶一張圖（檔案不存在也算附件，會是遺失佔位）。</summary>
    private static ToolCardViewModel ImageCard(string callId)
    {
        var card = Card(callId, "generate_image");
        card.Complete(new ToolResultBlock(callId, "ok", false, "產生圖片", null, null, 0, ToolApproval.NotRequired,
            ImageFiles: ["/nonexistent/lumi-test.png"]));
        return card;
    }

    private static ToolRunViewModel Run(params ToolCardViewModel[] cards)
    {
        var run = new ToolRunViewModel();
        foreach (var card in cards)
            run.Cards.Add(card);
        return run;
    }

    private static MessageViewModel User(string text) => MessageViewModel.User(text);

    /// <summary>一則完成的助理文字訊息。</summary>
    private static MessageViewModel Assistant(string text) =>
        MessageViewModel.From(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow));

    private static MessageViewModel Divider() => new CompactionDividerViewModel("摘要");

    [Fact]
    public void Attachments_GoToTheFirstAssistantTextAfterTheTools()
    {
        var run = Run(TarotCard("t1"));
        var reply = Assistant("解讀");
        var items = new List<MessageViewModel> { User("抽牌"), run, reply };

        ReplyAttachmentLayout.Apply(items);

        Assert.Equal(3, items.Count);
        Assert.Single(reply.ReplyAttachments);
        Assert.IsType<TarotAttachmentViewModel>(reply.ReplyAttachments[0]);
        Assert.False(run.HasReplyAttachments);
    }

    [Fact]
    public void WithoutText_AnAttachmentHostIsInsertedRightAfterTheTools()
    {
        var run = Run(TarotCard("t1"));
        var items = new List<MessageViewModel> { User("抽牌"), run };

        ReplyAttachmentLayout.Apply(items);

        Assert.Equal(3, items.Count);
        var host = Assert.IsType<MessageViewModel>(items[2]);
        Assert.True(host.IsAttachmentHost);
        Assert.False(host.HasText);
        Assert.False(host.ShowBubble);
        Assert.Equal(run.CreatedAt, host.CreatedAt);
        Assert.Single(host.ReplyAttachments);
    }

    [Fact]
    public void AUserMessageAfterTheTools_GetsAnAttachmentHostBeforeIt()
    {
        var run = Run(TarotCard("t1"));
        var next = User("下一個問題");
        var items = new List<MessageViewModel> { run, next };

        ReplyAttachmentLayout.Apply(items);

        Assert.True(items[1].IsAttachmentHost);
        Assert.Same(next, items[2]);
        Assert.Single(items[1].ReplyAttachments);
    }

    [Fact]
    public void CompactionDividersAreSkipped_WhenLookingForTheReply()
    {
        var run = Run(TarotCard("t1"));
        var reply = Assistant("解讀");
        var items = new List<MessageViewModel> { run, Divider(), reply };

        ReplyAttachmentLayout.Apply(items);

        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, m => m.IsAttachmentHost);
        Assert.Single(reply.ReplyAttachments);
    }

    [Fact]
    public void EachToolRun_KeepsItsOwnAttachments()
    {
        var first = Run(TarotCard("t1"));
        var firstReply = Assistant("A");
        var second = Run(ImageCard("i1"));
        var secondReply = Assistant("B");
        var items = new List<MessageViewModel> { User("q1"), first, firstReply, User("q2"), second, secondReply };

        ReplyAttachmentLayout.Apply(items);

        Assert.Equal(6, items.Count);
        Assert.IsType<TarotAttachmentViewModel>(Assert.Single(firstReply.ReplyAttachments));
        Assert.IsType<ImageAttachmentViewModel>(Assert.Single(secondReply.ReplyAttachments));
    }

    [Fact]
    public void AttachmentHostIsRemoved_WhenARealAssistantMessageAppears()
    {
        var run = Run(TarotCard("t1"));
        var host = MessageViewModel.AttachmentHost(run.CreatedAt);
        var reply = Assistant("解讀");
        var items = new List<MessageViewModel> { run, host, reply };

        ReplyAttachmentLayout.Apply(items);

        Assert.DoesNotContain(host, items);
        Assert.Empty(host.ReplyAttachments);
        Assert.Single(reply.ReplyAttachments);
    }

    [Fact]
    public void AttachmentHostIsRemoved_WhenTheAttachmentsAreGone()
    {
        var run = Run(Card("e1"));   // 沒有完成的一般工具：沒有附件
        var host = MessageViewModel.AttachmentHost(run.CreatedAt);
        var items = new List<MessageViewModel> { run, host };

        ReplyAttachmentLayout.Apply(items);

        Assert.Single(items);
        Assert.Same(run, items[0]);
        Assert.Empty(host.ReplyAttachments);
    }

    [Fact]
    public void ExistingAttachmentHost_IsReusedNotRecreated()
    {
        var run = Run(TarotCard("t1"));
        var host = MessageViewModel.AttachmentHost(run.CreatedAt);
        var items = new List<MessageViewModel> { run, host };

        ReplyAttachmentLayout.Apply(items);

        Assert.Same(host, items[1]);
        Assert.Single(host.ReplyAttachments);
    }

    [Fact]
    public void UnchangedAttachments_DoNotRebuildTheCollection()
    {
        var run = Run(TarotCard("t1"));
        var reply = Assistant("解讀");
        var items = new List<MessageViewModel> { run, reply };
        ReplyAttachmentLayout.Apply(items);

        var changes = 0;
        reply.ReplyAttachments.CollectionChanged += (_, _) => changes++;
        ReplyAttachmentLayout.Apply(items);

        Assert.Equal(0, changes);
        Assert.Single(reply.ReplyAttachments);
    }

    [Fact]
    public void MessagesThatAreNotHosts_AreCleared()
    {
        var reply = Assistant("普通回答");
        reply.ReplyAttachments.Add(ImageAttachmentViewModel.Pending("x", 1024, 1024));
        var items = new List<MessageViewModel> { User("hi"), reply };

        ReplyAttachmentLayout.Apply(items);

        Assert.Empty(reply.ReplyAttachments);
    }

    [Fact]
    public void MarkStopped_OnlyFlagsTheLastHostWhenAStoppedCardIsThere()
    {
        var stopped = Card("s1");
        stopped.Complete(new ToolResultBlock("s1", "使用者已停止。", true, "回聲", null, "已停止", 0, ToolApproval.NotRequired));
        var run = Run(TarotCard("t1"), stopped);
        var items = new List<MessageViewModel> { User("抽牌"), run };
        ReplyAttachmentLayout.Apply(items);

        ReplyAttachmentLayout.MarkStopped(items);

        var host = items[^1];
        Assert.True(host.IsAttachmentHost);
        Assert.True(host.IsStopped);
        Assert.True(host.ShowStoppedNote);
    }

    [Fact]
    public void MarkStopped_DoesNothingWithoutAStoppedCard()
    {
        var run = Run(TarotCard("t1"));
        var items = new List<MessageViewModel> { run };
        ReplyAttachmentLayout.Apply(items);

        ReplyAttachmentLayout.MarkStopped(items);

        Assert.False(items[^1].IsStopped);
    }

    [Fact]
    public void OpenRunForNewCall_AppendsToTheRunBeforeTheHost()
    {
        var run = Run(TarotCard("t1"));
        var items = new List<MessageViewModel> { run };
        ReplyAttachmentLayout.Apply(items);

        Assert.Same(run, ReplyAttachmentLayout.OpenRunForNewCall(items));
        Assert.Null(ReplyAttachmentLayout.OpenRunForNewCall([User("hi")]));
    }

    // ---- 只放附件的訊息的狀態（MessageViewModel） ----

    [Fact]
    public void TypingDots_AndTypingIndicator_AreExclusive()
    {
        var message = MessageViewModel.StreamingAssistant();
        Assert.True(message.ShowTypingIndicator);
        Assert.False(message.ShowTypingDots);

        message.ReplyAttachments.Add(ImageAttachmentViewModel.Pending("c1", 1024, 1024));

        Assert.False(message.ShowTypingIndicator);
        Assert.True(message.ShowTypingDots);
        Assert.True(message.ShowBubble);

        message.AppendText("好", DateTimeOffset.UtcNow);

        Assert.False(message.ShowTypingDots);
        Assert.False(message.ShowTypingIndicator);
    }

    [Fact]
    public void AttachmentHost_HasNoBubble_AndCannotCopy()
    {
        var host = MessageViewModel.AttachmentHost();

        Assert.False(host.ShowBubble);
        Assert.False(host.CanCopy);
        Assert.False(host.ShowActions);   // 不是最後一則
        host.IsLastAssistant = true;
        Assert.True(host.ShowActions);     // 最後一則：只有重新產生
    }

    [Fact]
    public void AttachmentHost_ShowsTheStoppedNoteInsteadOfABubble()
    {
        var host = MessageViewModel.AttachmentHost();
        Assert.False(host.ShowStoppedNote);

        host.IsStopped = true;

        Assert.True(host.ShowStoppedNote);
        Assert.False(host.ShowBubble);
    }

    [Fact]
    public void CanCopy_FollowsTheText()
    {
        var message = Assistant("好");
        Assert.True(message.CanCopy);

        message.Text = "";

        Assert.False(message.CanCopy);
    }
}
