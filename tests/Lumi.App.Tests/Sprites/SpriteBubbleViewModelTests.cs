using Lumi.App.Resources;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools;

namespace Lumi.App.Tests.Sprites;

/// <summary>ui-spec 8.1（氣泡）與 8.3（氣泡內的權限確認）。</summary>
public class SpriteBubbleViewModelTests
{
    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new();
        public RecordingResponder Responder { get; } = new();
        public SpriteBubbleViewModel Bubble { get; }
        public List<string?> Opened { get; } = [];
        public int Retries { get; private set; }
        public int SettingsOpened { get; private set; }
        public int Dismissals { get; private set; }

        public Rig(SpriteShellOptions? options = null)
        {
            Bubble = new SpriteBubbleViewModel(options ?? new SpriteShellOptions(), Time, Responder);
            Bubble.OpenWorkbenchRequested += Opened.Add;
            Bubble.RetryRequested += () => Retries++;
            Bubble.OpenSettingsRequested += () => SettingsOpened++;
            Bubble.Dismissed += () => Dismissals++;
        }

        public void Feed(params ChatEvent[] events)
        {
            foreach (var e in events)
                Bubble.OnEvent(e);
        }

        public void Tick(double seconds)
        {
            Time.Advance(TimeSpan.FromSeconds(seconds));
            Bubble.Tick();
        }
    }

    // ---- 狀態與串流 ----

    [Fact]
    public void ItStartsHidden()
    {
        Assert.Equal(SpriteBubbleKind.Hidden, new Rig().Bubble.Kind);
        Assert.False(new Rig().Bubble.IsVisible);
    }

    [Fact]
    public void RunStarted_ShowsOneLineOfStatus_NotTheUsersQuestion()
    {
        var rig = new Rig();

        rig.Bubble.OnRunStarted();

        Assert.Equal(SpriteBubbleKind.Status, rig.Bubble.Kind);
        Assert.Equal(Strings.Sprite_StatusThinking, rig.Bubble.StatusText);
        Assert.Equal("", rig.Bubble.Text);
    }

    [Fact]
    public void ToolCalls_ShowTheActionAndTarget_LikeTheToolCardTitle()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Feed(Ev.Tool(display: "讀取檔案", target: "plan/00-master-plan.md"));

        Assert.Equal("讀取檔案 · plan/00-master-plan.md", rig.Bubble.StatusText);
        Assert.True(rig.Bubble.IsStatus);

        rig.Feed(Ev.Tool("c2", display: "目前時間", target: null));
        Assert.Equal("目前時間", rig.Bubble.StatusText);
    }

    [Fact]
    public void TheReplyIsStreamedIntoTheBubble_ThenDone()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Feed(new TextDelta("明天"));
        Assert.Equal(SpriteBubbleKind.Streaming, rig.Bubble.Kind);
        rig.Feed(new TextDelta("會下雨。"));
        Assert.Equal("明天會下雨。", rig.Bubble.Text);

        rig.Feed(Ev.Done("明天會下雨。"));
        rig.Bubble.OnRunFinished(Ev.Finished());
        Assert.Equal(SpriteBubbleKind.Done, rig.Bubble.Kind);
        Assert.True(rig.Bubble.IsAnswer);
        Assert.False(rig.Bubble.ShowDetailsLink);
    }

    [Fact]
    public void OnlyTheLatestMessageIsShown_TextBeforeAToolCallIsReplaced()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(new TextDelta("我來查一下。"), Ev.Tool(), Ev.ToolTurnDone(), Ev.ToolDone());
        Assert.True(rig.Bubble.IsStatus);

        rig.Feed(new TextDelta("查到了。"));

        Assert.Equal("查到了。", rig.Bubble.Text);   // 不是「我來查一下。查到了。」
    }

    [Fact]
    public void ANewRun_ClearsThePreviousAnswer()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(new TextDelta("第一個答案"), Ev.Done("第一個答案"));
        rig.Bubble.OnRunFinished(Ev.Finished());

        rig.Bubble.OnRunStarted();

        Assert.Equal("", rig.Bubble.Text);
        Assert.Equal(SpriteBubbleKind.Status, rig.Bubble.Kind);
    }

    [Fact]
    public void WithoutStreamedText_TheFinalMessageIsUsed()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Feed(Ev.Done("一次給完"));

        Assert.Equal("一次給完", rig.Bubble.Text);
    }

    // ---- 截斷與完整內容 ----

    [Fact]
    public void LongText_IsCutAtTheClusterLimit_AndOffersTheFullContent()
    {
        var rig = new Rig(new SpriteShellOptions { BubbleMaxClusters = 10 });
        rig.Bubble.OnRunStarted();

        rig.Feed(new TextDelta("一二三四五六七八九十十一十二"), Ev.Done("一二三四五六七八九十十一十二"));

        Assert.Equal("一二三四五六七八九十…", rig.Bubble.Text);
        Assert.True(rig.Bubble.IsTruncated);
        Assert.True(rig.Bubble.ShowDetailsLink);
    }

    [Fact]
    public void TextExactlyAtTheLimit_IsNotCut()
    {
        var (text, cut) = SpriteBubbleViewModel.Truncate("一二三四五六七八九十", 10);

        Assert.Equal("一二三四五六七八九十", text);
        Assert.False(cut);
    }

    [Fact]
    public void Truncation_NeverSplitsAnEmojiOrACombinedCharacter()
    {
        // ❤️‍🔥 是 ZWJ 組合（多個碼位），👨‍👩‍👧 是家庭，都只算一個叢集
        var (text, cut) = SpriteBubbleViewModel.Truncate("甲❤️‍🔥乙👨‍👩‍👧丙丁", 4);

        Assert.True(cut);
        Assert.Equal("甲❤️‍🔥乙👨‍👩‍👧…", text);
    }

    [Fact]
    public void ManyCodeUnitsButFewClusters_IsNotCut()
    {
        var emojis = string.Concat(Enumerable.Repeat("👨‍👩‍👧", 5));   // 5 個叢集、35 個字元

        Assert.False(SpriteBubbleViewModel.Truncate(emojis, 5).Truncated);
        Assert.True(SpriteBubbleViewModel.Truncate(emojis, 4).Truncated);
    }

    [Fact]
    public void Truncation_OnlyAffectsTheBubble()
    {
        var rig = new Rig(new SpriteShellOptions { BubbleMaxClusters = 5 });
        rig.Bubble.OnRunStarted();
        var message = Ev.Done("一二三四五六七八九十");

        rig.Feed(new TextDelta("一二三四五六七八九十"), message);

        Assert.Equal("一二三四五…", rig.Bubble.Text);
        Assert.Equal("一二三四五六七八九十", Assert.IsType<TextBlock>(message.Message.Content[0]).Text);   // 存檔的訊息是完整的
    }

    [Fact]
    public void ShowDetails_OffersTheLinkOnlyAfterTheToolSucceeded_AndOpensItsCard()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Tool("d1", ShowDetailsTool.Name, "整理完整內容", "C# 讀檔範例"));
        Assert.False(rig.Bubble.ShowDetailsLink);

        rig.Feed(Ev.ToolTurnDone(), Ev.ToolDone("d1"), new TextDelta("放到工作台了。"), Ev.Done("放到工作台了。"));

        Assert.True(rig.Bubble.ShowDetailsLink);
        Assert.True(rig.Bubble.IsInteractive);
        rig.Bubble.OpenDetailsCommand.Execute(null);
        Assert.Equal(["d1"], rig.Opened);   // 工作台捲到那張內容卡
    }

    [Fact]
    public void AFailedShowDetails_OffersNothing()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Tool("d1", ShowDetailsTool.Name, "整理完整內容", "x"), Ev.ToolDone("d1", error: true), Ev.Done("抱歉"));

        Assert.False(rig.Bubble.ShowDetailsLink);
    }

    [Fact]
    public void HittingTheOutputLimit_OffersTheFullContent()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Feed(new TextDelta("說到一半"), Ev.Done("說到一半", StopReason.MaxTokens));

        Assert.True(rig.Bubble.ShowDetailsLink);
    }

    [Fact]
    public void OpeningTheFullContentWithoutAShowDetailsCard_OpensTheWorkbenchWithoutATarget()
    {
        var rig = new Rig(new SpriteShellOptions { BubbleMaxClusters = 3 });
        rig.Bubble.OnRunStarted();
        rig.Feed(new TextDelta("一二三四五"), Ev.Done("一二三四五"));

        rig.Bubble.OpenDetailsCommand.Execute(null);

        Assert.Equal([(string?)null], rig.Opened);
    }

    // ---- 淡出 ----

    [Fact]
    public void AfterDone_TheBubbleFadesAfterEightSeconds()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(new TextDelta("好"), Ev.Done("好"));
        rig.Bubble.OnRunFinished(Ev.Finished());

        rig.Tick(7);
        Assert.True(rig.Bubble.IsVisible);
        rig.Tick(1.5);
        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(1, rig.Dismissals);
    }

    [Fact]
    public void WhileTheCursorIsOverIt_TheFadeTimerPauses()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(new TextDelta("好"), Ev.Done("好"));
        rig.Tick(5);

        rig.Bubble.SetHovered(true);
        rig.Tick(60);
        Assert.True(rig.Bubble.IsVisible);

        rig.Bubble.SetHovered(false);
        rig.Tick(2);
        Assert.True(rig.Bubble.IsVisible);   // 還剩 3 秒
        rig.Tick(1.5);
        Assert.False(rig.Bubble.IsVisible);
    }

    [Fact]
    public void StatusStreamingApprovalAndError_NeverFadeOnTheirOwn()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Tick(600);
        Assert.True(rig.Bubble.IsVisible);

        rig.Feed(new TextDelta("…"));
        rig.Tick(600);
        Assert.True(rig.Bubble.IsVisible);

        rig.Feed(Ev.Approval());
        rig.Tick(600);
        Assert.True(rig.Bubble.IsApproval);

        rig.Bubble.OnRunFinished(Ev.Finished(Ev.Failure()));
        rig.Tick(600);
        Assert.True(rig.Bubble.IsError);
    }

    [Fact]
    public void AStoppedRun_HidesTheBubble()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(new TextDelta("說到一半"));

        rig.Bubble.OnRunFinished(Ev.Finished(cancelled: true));

        Assert.False(rig.Bubble.IsVisible);
    }

    // ---- 權限確認 ----

    [Fact]
    public void ApprovalRequired_ShowsTheCompactConfirmation()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Feed(Ev.Tool("c1", display: "寫入檔案", permission: ToolPermission.Write), Ev.Approval("c1", "寫入檔案", "notes/a.md"));

        Assert.True(rig.Bubble.IsApproval);
        Assert.True(rig.Bubble.IsInteractive);
        var approval = rig.Bubble.Approval!;
        Assert.Equal("要執行「寫入檔案」嗎？", approval.Question);
        Assert.Equal("notes/a.md", approval.Target);
        Assert.True(approval.HasTarget);
        Assert.Equal(Strings.Sprite_Allow, approval.AllowLabel);
        Assert.True(approval.ShowMoreLink);
        Assert.False(approval.ShowOptions);
        Assert.False(approval.IsDestructive);
    }

    [Fact]
    public void TheCompactAllowButton_IsAllowOnce()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1"));

        rig.Bubble.AllowOnceCommand.Execute(null);

        Assert.Equal([("c1", ApprovalDecision.AllowOnce)], rig.Responder.Calls);
    }

    [Fact]
    public void MoreOptions_ExpandsInPlace_WithAllowForConversation()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1"));

        rig.Bubble.ToggleMoreOptionsCommand.Execute(null);
        var approval = rig.Bubble.Approval!;
        Assert.True(approval.ShowOptions);
        Assert.False(approval.ShowMoreLink);

        rig.Bubble.AllowForConversationCommand.Execute(null);
        Assert.Equal([("c1", ApprovalDecision.AllowForConversation)], rig.Responder.Calls);
    }

    [Fact]
    public void Deny_ResolvesWithDeny()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1"));

        rig.Bubble.DenyCommand.Execute(null);

        Assert.Equal([("c1", ApprovalDecision.Deny)], rig.Responder.Calls);
    }

    [Fact]
    public void ViewInWorkbench_OpensTheWorkbenchAtThatToolCard()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c9"));

        rig.Bubble.ViewInWorkbenchCommand.Execute(null);

        Assert.Equal(["c9"], rig.Opened);
    }

    [Fact]
    public void Destructive_IsMarkedIrreversible_ListsEverything_AndHasNoMoreOptions()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Feed(Ev.Approval("c1", "刪除筆記", "notes/a.md", ToolPermission.Destructive, detail: "notes/a.md\nnotes/b.md\nnotes/c.md"));

        var approval = rig.Bubble.Approval!;
        Assert.True(approval.IsDestructive);
        Assert.Equal(Strings.Sprite_AllowDestructive, approval.AllowLabel);
        Assert.Contains("無法復原", approval.Question);
        Assert.True(approval.ShowDetail);
        Assert.Contains("notes/c.md", approval.Detail);
        Assert.False(approval.CanExpand);
        Assert.False(approval.ShowMoreLink);

        rig.Bubble.ToggleMoreOptionsCommand.Execute(null);
        Assert.False(approval.ShowOptions);   // 沒有展開
    }

    [Fact]
    public void AfterAnswering_TheBubbleReturnsToTheToolStatus()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1", "寫入檔案"));

        rig.Bubble.OnApprovalResolved("c1", ApprovalDecision.AllowOnce);

        Assert.Equal(SpriteBubbleKind.Status, rig.Bubble.Kind);
        Assert.Equal("已允許 · 寫入檔案中…", rig.Bubble.StatusText);
        Assert.Null(rig.Bubble.Approval);
    }

    [Fact]
    public void AfterDenying_TheStatusSaysSo()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1", "寫入檔案"));

        rig.Bubble.OnApprovalResolved("c1", ApprovalDecision.Deny);

        Assert.Equal("已拒絕 · 寫入檔案", rig.Bubble.StatusText);
    }

    [Fact]
    public void AnAnswerGivenInTheWorkbench_AlsoReturnsTheBubbleToStatus()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1"));

        rig.Bubble.OnApprovalResolved("c1", ApprovalDecision.AllowForConversation);   // 來自 Resolved 事件，不是氣泡的按鈕

        Assert.False(rig.Bubble.IsApproval);
        Assert.Empty(rig.Responder.Calls);
    }

    [Fact]
    public void AnAnswerForAnotherCall_IsIgnored()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1"));

        rig.Bubble.OnApprovalResolved("zzz", ApprovalDecision.Deny);

        Assert.True(rig.Bubble.IsApproval);
    }

    [Fact]
    public void ThinkingWhileWaitingForApproval_DoesNotReplaceTheConfirmation()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(Ev.Approval("c1"), new ThinkingDelta("…"), new TextDelta("…"));

        Assert.True(rig.Bubble.IsApproval);
    }

    // ---- 錯誤 ----

    [Fact]
    public void AFailure_ShowsOneSentence_WithRetryAndViewInWorkbench()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Feed(new Failed(Ev.Failure(ChatFailureKind.Network, "連不上伺服器")));

        rig.Bubble.OnRunFinished(Ev.Finished(Ev.Failure(ChatFailureKind.Network, "連不上伺服器")));

        Assert.True(rig.Bubble.IsError);
        Assert.Equal("連不上伺服器", rig.Bubble.ErrorText);
        Assert.True(rig.Bubble.CanRetry);
        Assert.False(rig.Bubble.CanOpenSettings);

        rig.Bubble.RetryCommand.Execute(null);
        Assert.Equal(1, rig.Retries);
        Assert.False(rig.Bubble.IsVisible);

        rig.Bubble.OnRunFinished(Ev.Finished(Ev.Failure()));
        rig.Bubble.ViewInWorkbenchCommand.Execute(null);
        Assert.Equal([(string?)null], rig.Opened);
    }

    [Theory]
    [InlineData(ChatFailureKind.MissingApiKey)]
    [InlineData(ChatFailureKind.InvalidApiKey)]
    [InlineData(ChatFailureKind.SecretUnreadable)]
    public void KeyProblems_OfferToOpenTheSettings(ChatFailureKind kind)
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Bubble.OnRunFinished(Ev.Finished(Ev.Failure(kind, "金鑰有問題", retryable: false)));

        Assert.True(rig.Bubble.CanOpenSettings);
        Assert.True(rig.Bubble.CanRetry);   // 存好金鑰後可以重試
        rig.Bubble.OpenSettingsCommand.Execute(null);
        Assert.Equal(1, rig.SettingsOpened);
    }

    [Fact]
    public void AnUnexpectedError_UsesTheGenericSentence()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Bubble.OnRunFinished(Ev.Finished(unexpected: true));

        Assert.Equal(Strings.Workbench_UnexpectedError, rig.Bubble.ErrorText);
    }

    [Fact]
    public void MissingProgramFiles_ShowTheirOwnSentence_WithoutRetry()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        // ChatSession 在例外時只回報結果（沒有 Failed 事件）
        rig.Bubble.OnRunFinished(Ev.Finished(Ev.Failure(ChatFailureKind.AppFilesMissing, ChatSession.AppFilesMissingMessage, retryable: false)));

        Assert.Equal(SpriteBubbleKind.Error, rig.Bubble.Kind);
        Assert.Equal(Strings.Chat_ErrorAppFilesMissing, rig.Bubble.ErrorText);
        Assert.False(rig.Bubble.CanRetry);
        Assert.False(rig.Bubble.CanOpenSettings);
    }

    [Fact]
    public void ANonRetryableFailure_HasNoRetryButton()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();

        rig.Bubble.OnRunFinished(Ev.Finished(Ev.Failure(ChatFailureKind.BadRequest, "請求內容有問題", retryable: false)));

        Assert.False(rig.Bubble.CanRetry);
    }

    [Fact]
    public void DismissingAnError_HidesIt_AndTellsTheShell()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted();
        rig.Bubble.OnRunFinished(Ev.Finished(Ev.Failure()));

        rig.Bubble.DismissCommand.Execute(null);

        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(1, rig.Dismissals);
    }
}
