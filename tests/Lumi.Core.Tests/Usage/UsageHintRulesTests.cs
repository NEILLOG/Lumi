using Lumi.Core.Context;
using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>M23 23I：使用習慣提示的判斷（plan/23-usage-cost.md 第 7.2 節）。每條規則都測「剛好達到」與「差一點」。</summary>
public sealed class UsageHintRulesTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>測試用的對話：依序加訊息，Seq 從 1 起。</summary>
    private sealed class History
    {
        private readonly List<ChatMessage> _messages = [];

        public IReadOnlyList<ChatMessage> Messages => _messages;

        public History Add(ChatRole role, IReadOnlyList<ContentBlock> content, TokenUsage? usage = null)
        {
            var seq = _messages.Count + 1;
            _messages.Add(new ChatMessage(ChatMessage.NewId(), role, content, Start.AddMinutes(seq),
                ModelId: role == ChatRole.Assistant ? "model-x" : null, Usage: usage) { Seq = seq });
            return this;
        }

        public History User(string text = "問") => Add(ChatRole.User, [new TextBlock(text)]);

        public History UserWith(params ContentBlock[] blocks) => Add(ChatRole.User, blocks);

        public History Assistant(TokenUsage? usage = null, string text = "答") => Add(ChatRole.Assistant, [new TextBlock(text)], usage);

        /// <summary>只加助理訊息（不加使用者訊息）。</summary>
        public History Replies(int count, TokenUsage? usage = null)
        {
            for (var i = 0; i < count; i++)
                Assistant(usage);
            return this;
        }

        /// <summary>使用者訊息加一則助理回覆，重複 <paramref name="count"/> 次。</summary>
        public History Exchanges(int count, TokenUsage? usage = null)
        {
            for (var i = 0; i < count; i++)
                User().Assistant(usage);
            return this;
        }
    }

    private static TokenUsage Usage(int input = 0, int output = 0, int cacheWrite = 0, int cacheRead = 0) =>
        new(InputTokens: input, OutputTokens: output, CacheCreationInputTokens: cacheWrite, CacheReadInputTokens: cacheRead);

    private static HintInput Input(
        History history, CompactionState? compaction = null, int contextUsed = 0, int contextLimit = 200_000,
        int turns = 1, int toolTokens = 0, bool first = false) =>
        new(history.Messages, compaction ?? CompactionState.Empty, contextUsed, contextLimit, turns, toolTokens, first);

    private static IReadOnlyList<string> Rules(HintInput input) =>
        UsageHintRules.AfterResponse(input).Select(m => m.Rule).ToList();

    private static UsageHintMatch Single(HintInput input, string rule) =>
        UsageHintRules.AfterResponse(input).Single(m => m.Rule == rule);

    /// <summary>估算剛好 <paramref name="tokens"/> 個 tokens 的檔案（ASCII：每 3 個字元 1 個 token）。</summary>
    private static ContextBlock File(string name, int tokens) =>
        new(ContextKind.File, name, "/work/" + name, new string('a', tokens * 3 - 2), Embedded: true);

    /// <summary>估算剛好 <paramref name="tokens"/> 個 tokens 的段落。</summary>
    private static string Paragraph(int tokens) => new('a', tokens * 3 - 2);

    private static ImageBlock Image(string name) => new(name, "image/png", name);

    private const string CodeReply = "```csharp\nvar x = 1;\n```";

    // ---- 第 2 條 long-conversation ----

    [Fact]
    public void LongConversation_TriggersAtExactly60PercentOfContext()
    {
        var input = Input(new History().Exchanges(1), contextUsed: 120_000, contextLimit: 200_000);

        Assert.Contains(UsageHintIds.LongConversation, Rules(input));
    }

    [Fact]
    public void LongConversation_NotTriggeredJustBelow60PercentOfContext()
    {
        var input = Input(new History().Exchanges(1), contextUsed: 119_999, contextLimit: 200_000);

        Assert.DoesNotContain(UsageHintIds.LongConversation, Rules(input));
    }

    [Fact]
    public void LongConversation_ContextRuleNeedsALimit()
    {
        var input = Input(new History().Exchanges(1), contextUsed: 500_000, contextLimit: 0);

        Assert.DoesNotContain(UsageHintIds.LongConversation, Rules(input));
    }

    [Fact]
    public void LongConversation_TriggersAt40TurnsWithRecentAverageExactly60K()
    {
        // 輸入 50K + 快取寫 5K + 快取讀 5K = 60K；輸出不算
        var history = new History().Exchanges(40, Usage(input: 50_000, output: 10_000, cacheWrite: 5_000, cacheRead: 5_000));

        var input = Input(history, turns: 40);

        Assert.Contains(UsageHintIds.LongConversation, Rules(input));
    }

    [Fact]
    public void LongConversation_NotTriggeredWithRecentAverageJustBelow60K()
    {
        var history = new History().Exchanges(40, Usage(input: 49_999, output: 10_000, cacheWrite: 5_000, cacheRead: 5_000));

        var input = Input(history, turns: 40);

        Assert.DoesNotContain(UsageHintIds.LongConversation, Rules(input));
    }

    [Fact]
    public void LongConversation_NotTriggeredBelow40Turns()
    {
        var history = new History().Exchanges(39, Usage(input: 100_000));

        var input = Input(history, turns: 39);

        Assert.DoesNotContain(UsageHintIds.LongConversation, Rules(input));
    }

    [Fact]
    public void LongConversation_OnlyTheLast10UsageRepliesCount()
    {
        // 前 30 則很大，最後 10 則剛好 59,999：平均不到 60K，不觸發
        var history = new History()
            .Exchanges(30, Usage(input: 500_000))
            .Exchanges(10, Usage(input: 59_999));

        var input = Input(history, turns: 40);

        Assert.DoesNotContain(UsageHintIds.LongConversation, Rules(input));
    }

    [Fact]
    public void LongConversation_NotTriggeredWithFewerThan10UsageReplies()
    {
        var history = new History().Exchanges(9, Usage(input: 100_000));

        var input = Input(history, turns: 40);

        Assert.DoesNotContain(UsageHintIds.LongConversation, Rules(input));
    }

    // ---- 第 4 條 big-file ----

    [Fact]
    public void BigFile_TriggersAtExactly15KTokensWith20RepliesAfter()
    {
        var history = new History().UserWith(File("ScheduleHistory.dc.html", 15_000)).Replies(20);

        var match = Single(Input(history), UsageHintIds.BigFile);

        Assert.Equal(["ScheduleHistory.dc.html", "15K"], match.Args);
        Assert.Equal("ScheduleHistory.dc.html", match.Key);
    }

    [Fact]
    public void BigFile_NotTriggeredJustBelow15K()
    {
        var history = new History().UserWith(File("a.txt", 14_999)).Replies(20);

        Assert.DoesNotContain(UsageHintIds.BigFile, Rules(Input(history)));
    }

    [Fact]
    public void BigFile_NotTriggeredWith19RepliesAfter()
    {
        var history = new History().UserWith(File("a.txt", 15_000)).Replies(19);

        Assert.DoesNotContain(UsageHintIds.BigFile, Rules(Input(history)));
    }

    [Fact]
    public void BigFile_FileBeforeTheSummaryIsNotCounted()
    {
        var history = new History().UserWith(File("a.txt", 15_000)).Replies(20);
        var compaction = new CompactionState(null, SummaryUptoSeq: 1, Summary: "摘要");

        Assert.DoesNotContain(UsageHintIds.BigFile, Rules(Input(history, compaction)));
    }

    [Fact]
    public void BigFile_PicksTheLargestFile()
    {
        var history = new History()
            .UserWith(File("small.txt", 16_000))
            .UserWith(File("big.txt", 20_000))
            .Replies(20);

        var match = Single(Input(history), UsageHintIds.BigFile);

        Assert.Equal("big.txt", match.Key);
        Assert.Equal(["big.txt", "20K"], match.Args);
    }

    // ---- 第 5 條 images ----

    [Fact]
    public void Images_TriggersAtExactly3ImagesWith10RepliesAfterTheFirst()
    {
        var history = new History().UserWith(Image("1"), Image("2"), Image("3")).Replies(10);

        var match = Single(Input(history), UsageHintIds.Images);

        Assert.Equal(["3"], match.Args);
        Assert.Null(match.Key);
    }

    [Fact]
    public void Images_NotTriggeredWith2ImagesOnly()
    {
        // 2 張 = 3,000 tokens，兩個條件都不到
        var history = new History().UserWith(Image("1"), Image("2")).Replies(10);

        Assert.DoesNotContain(UsageHintIds.Images, Rules(Input(history)));
    }

    [Fact]
    public void Images_NotTriggeredWith9RepliesAfterTheFirst()
    {
        var history = new History().UserWith(Image("1"), Image("2"), Image("3")).Replies(9);

        Assert.DoesNotContain(UsageHintIds.Images, Rules(Input(history)));
    }

    [Fact]
    public void Images_CountsAcrossMessagesAndUsesTheEarliestForTheReplyCount()
    {
        // 第一張之後有 1 + 1 + 10 = 12 則助理訊息
        var history = new History()
            .UserWith(Image("1")).Replies(1)
            .UserWith(Image("2")).Replies(1)
            .UserWith(Image("3")).Replies(10);

        var match = Single(Input(history), UsageHintIds.Images);

        Assert.Equal(["3"], match.Args);
    }

    [Fact]
    public void Images_SummarizedImagesAreNotCounted()
    {
        var history = new History().UserWith(Image("1"), Image("2"), Image("3")).Replies(10);
        var compaction = new CompactionState(null, SummaryUptoSeq: 1, Summary: "摘要");

        Assert.DoesNotContain(UsageHintIds.Images, Rules(Input(history, compaction)));
    }

    // ---- 第 6 條 mcp-tools ----

    [Fact]
    public void McpTools_TriggersAtExactly10KOnTheFirstResponse()
    {
        var input = Input(new History().Exchanges(1), toolTokens: 10_000, first: true);

        var match = Single(input, UsageHintIds.McpTools);

        Assert.Equal(["10K"], match.Args);
    }

    [Fact]
    public void McpTools_NotTriggeredJustBelow10K()
    {
        var input = Input(new History().Exchanges(1), toolTokens: 9_999, first: true);

        Assert.DoesNotContain(UsageHintIds.McpTools, Rules(input));
    }

    [Fact]
    public void McpTools_NotTriggeredAfterTheFirstResponse()
    {
        var input = Input(new History().Exchanges(2), toolTokens: 20_000, first: false);

        Assert.DoesNotContain(UsageHintIds.McpTools, Rules(input));
    }

    // ---- 第 8 條 long-output ----

    private static readonly TokenUsage LongOutput = Usage(input: 100, output: 4_000);

    [Fact]
    public void LongOutput_TriggersOnTheThirdLongCodeReply()
    {
        var history = new History()
            .User().Assistant(LongOutput, CodeReply)
            .User().Assistant(LongOutput, CodeReply)
            .User().Assistant(LongOutput, CodeReply);

        Assert.Contains(UsageHintIds.LongOutput, Rules(Input(history)));
    }

    [Fact]
    public void LongOutput_NotTriggeredWhenOutputJustBelow4K()
    {
        var justBelow = Usage(input: 100, output: 3_999);
        var history = new History()
            .User().Assistant(justBelow, CodeReply)
            .User().Assistant(justBelow, CodeReply)
            .User().Assistant(justBelow, CodeReply);

        Assert.DoesNotContain(UsageHintIds.LongOutput, Rules(Input(history)));
    }

    [Fact]
    public void LongOutput_NotTriggeredWithOnlyTwoLongCodeReplies()
    {
        var history = new History()
            .User().Assistant(LongOutput, CodeReply)
            .User().Assistant(LongOutput, CodeReply);

        Assert.DoesNotContain(UsageHintIds.LongOutput, Rules(Input(history)));
    }

    [Fact]
    public void LongOutput_NotTriggeredWhenTheLastReplyDoesNotMatch()
    {
        var history = new History()
            .User().Assistant(LongOutput, CodeReply)
            .User().Assistant(LongOutput, CodeReply)
            .User().Assistant(LongOutput, CodeReply)
            .User().Assistant(LongOutput, "這次只是文字");

        Assert.DoesNotContain(UsageHintIds.LongOutput, Rules(Input(history)));
    }

    [Fact]
    public void LongOutput_NeedsACodeFence()
    {
        var history = new History()
            .User().Assistant(LongOutput, "長文字")
            .User().Assistant(LongOutput, "長文字")
            .User().Assistant(LongOutput, "長文字");

        Assert.DoesNotContain(UsageHintIds.LongOutput, Rules(Input(history)));
    }

    // ---- 第 9 條 repeat-paste ----

    private static readonly string LongText = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 400));   // 7,200 字元 → 2,400 tokens

    [Fact]
    public void RepeatPaste_TriggersWhenAParagraphIsPastedAgain()
    {
        var history = new History().User(LongText).Assistant().User(LongText);

        var match = Single(Input(history), UsageHintIds.RepeatPaste);

        Assert.Equal(["1"], match.Args);
    }

    [Fact]
    public void RepeatPaste_WhitespaceDifferencesAreTheSameContent()
    {
        // 空格換成「空格＋換行＋空格」（沒有空白行），正規化後相同
        var variant = LongText.Replace(" ", "   \n  ");
        var history = new History().User(LongText).Assistant().User(variant);

        Assert.Contains(UsageHintIds.RepeatPaste, Rules(Input(history)));
    }

    [Fact]
    public void RepeatPaste_ChangedContentIsNotTheSame()
    {
        var changed = LongText.Replace("dolor", "sit");
        var history = new History().User(LongText).Assistant().User(changed);

        Assert.DoesNotContain(UsageHintIds.RepeatPaste, Rules(Input(history)));
    }

    [Fact]
    public void RepeatPaste_ParagraphsBelow2KTokensDoNotCount()
    {
        var history = new History().User(Paragraph(1_999)).Assistant().User(Paragraph(1_999));

        Assert.DoesNotContain(UsageHintIds.RepeatPaste, Rules(Input(history)));
    }

    [Fact]
    public void RepeatPaste_ParagraphOf2KTokensCounts()
    {
        var history = new History().User(Paragraph(2_000)).Assistant().User(Paragraph(2_000));

        Assert.Contains(UsageHintIds.RepeatPaste, Rules(Input(history)));
    }

    [Fact]
    public void RepeatPaste_SplitsByBlankLines()
    {
        var history = new History()
            .User(LongText)
            .Assistant()
            .User("前言\n\n" + LongText + "\n \n後記");

        Assert.Contains(UsageHintIds.RepeatPaste, Rules(Input(history)));
    }

    [Fact]
    public void RepeatPaste_ReportsTheTurnOfTheFirstOccurrence()
    {
        var history = new History()
            .User("前言").Assistant()          // 第 1 回合
            .User("別的內容").Assistant()      // 第 2 回合
            .User(LongText).Assistant()        // 第 3 回合：第一次貼上
            .User("中間").Assistant()          // 第 4 回合
            .User(LongText);                   // 第 5 回合：又貼一次

        var match = Single(Input(history), UsageHintIds.RepeatPaste);

        Assert.Equal(["3"], match.Args);
    }

    [Fact]
    public void RepeatPaste_NeedsAnEarlierUserMessage()
    {
        var history = new History().User(LongText);

        Assert.DoesNotContain(UsageHintIds.RepeatPaste, Rules(Input(history)));
    }

    // ---- AfterResponse 的整體行為 ----

    [Fact]
    public void AfterResponse_ReturnsMatchesInPriorityOrder_AndNeverTheUserActionRules()
    {
        var history = new History().UserWith(File("big.md", 15_000)).Replies(20).User("再一句").Assistant();
        var input = Input(history, contextUsed: 130_000, toolTokens: 12_000, first: true);

        Assert.Equal(
            [UsageHintIds.LongConversation, UsageHintIds.BigFile, UsageHintIds.McpTools],
            Rules(input));
        Assert.DoesNotContain(UsageHintIds.ModelSwitch, Rules(input));
        Assert.DoesNotContain(UsageHintIds.Regenerate, Rules(input));
        Assert.DoesNotContain(UsageHintIds.StaleResume, Rules(input));
    }

    [Fact]
    public void AfterResponse_NothingMatchesAnEmptyConversation()
    {
        Assert.Empty(UsageHintRules.AfterResponse(Input(new History())));
    }

    // ---- OnResume（第 3 條 stale-resume）----

    private static ChatMessage LastReply(DateTimeOffset at) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("答")], at, ModelId: "model-x");

    [Fact]
    public void OnResume_NoAssistantMessageIsNoHint()
    {
        Assert.Null(UsageHintRules.OnResume(null, 100_000, Now));
    }

    [Fact]
    public void OnResume_TriggersAtExactly60MinutesAndExactly50K()
    {
        var match = UsageHintRules.OnResume(LastReply(Now - TimeSpan.FromMinutes(60)), 50_000, Now);

        Assert.NotNull(match);
        Assert.Equal(UsageHintIds.StaleResume, match.Rule);
        Assert.Equal(["h:1", "50K"], match.Args);
        Assert.Null(match.Key);
    }

    [Fact]
    public void OnResume_NotTriggeredJustUnder60Minutes()
    {
        Assert.Null(UsageHintRules.OnResume(LastReply(Now - TimeSpan.FromMinutes(59)), 100_000, Now));
    }

    [Fact]
    public void OnResume_NotTriggeredJustUnder50KContext()
    {
        Assert.Null(UsageHintRules.OnResume(LastReply(Now - TimeSpan.FromHours(3)), 49_999, Now));
    }

    [Fact]
    public void OnResume_UsesWholeHoursUnder24Hours()
    {
        var match = UsageHintRules.OnResume(LastReply(Now - TimeSpan.FromHours(3) - TimeSpan.FromMinutes(30)), 92_000, Now);

        Assert.Equal(["h:3", "92K"], match!.Args);
    }

    [Fact]
    public void OnResume_JustUnder24HoursIsStillHours()
    {
        var match = UsageHintRules.OnResume(LastReply(Now - TimeSpan.FromHours(23) - TimeSpan.FromMinutes(59)), 60_000, Now);

        Assert.Equal(["h:23", "60K"], match!.Args);
    }

    [Fact]
    public void OnResume_UsesWholeDaysFromExactly24Hours()
    {
        var atDay = UsageHintRules.OnResume(LastReply(Now - TimeSpan.FromHours(24)), 60_000, Now);
        var twoDays = UsageHintRules.OnResume(LastReply(Now - TimeSpan.FromDays(2) - TimeSpan.FromHours(5)), 60_000, Now);

        Assert.Equal(["d:1", "60K"], atDay!.Args);
        Assert.Equal(["d:2", "60K"], twoDays!.Args);
    }
}
