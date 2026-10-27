using Lumi.Core.Chat;

namespace Lumi.Core.Tests.Chat;

/// <summary>M22 22H：整理狀態套到歷史上（純函式，不改存檔）。</summary>
public class CompactionApplierTests
{
    private static ChatMessage Msg(int seq, ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, DateTimeOffset.UtcNow) { Seq = seq };

    private static ChatMessage User(int seq, string text) => Msg(seq, ChatRole.User, new TextBlock(text));

    private static ChatMessage Assistant(int seq, params ContentBlock[] content) => Msg(seq, ChatRole.Assistant, content);

    private static ToolResultBlock Result(string id, string content, string target = "a.txt") =>
        new(id, content, false, "讀取檔案", target, "ok", 1, ToolApproval.NotRequired, [new ImageBlock("f.png", "image/png", "f")]);

    /// <summary>
    /// 三輪：1 使用者、2 助理(thinking+tool_use)、3 工具結果、4 助理；5 使用者、6 助理(tool_use)、7 工具結果、8 助理；9 使用者、10 助理(thinking+tool_use)、11 工具結果。
    /// </summary>
    private static List<ChatMessage> ThreeTurns() =>
    [
        User(1, "第一題"),
        Assistant(2, new ThinkingBlock("想1", "sig1"), new ToolUseBlock("u1", "read_file", "{}")),
        Msg(3, ChatRole.Tool, Result("u1", "第一個很長的結果")),
        Assistant(4, new TextBlock("第一個答案")),
        User(5, "第二題"),
        Assistant(6, new ThinkingBlock("想2", "sig2"), new ToolUseBlock("u2", "read_file", "{}")),
        Msg(7, ChatRole.Tool, Result("u2", "第二個很長的結果")),
        Assistant(8, new TextBlock("第二個答案")),
        User(9, "第三題"),
        Assistant(10, new ThinkingBlock("想3", "sig3"), new ToolUseBlock("u3", "read_file", "{}")),
        Msg(11, ChatRole.Tool, Result("u3", "第三個很長的結果")),
    ];

    [Fact]
    public void NoCompaction_ReturnsTheSameHistory()
    {
        var history = ThreeTurns();

        Assert.Same(history, CompactionApplier.Apply(history, CompactionState.Empty));
        // 送出的內容與沒有這個功能時完全相同（thinking 也不動）
        var request = ConversationService.BuildRequestMessages(CompactionApplier.Apply(history, CompactionState.Empty));
        Assert.Contains(request, m => m.Content.Any(b => b is ThinkingBlock));
    }

    [Fact]
    public void Boundaries_AreBeforeTheThirdAndSecondLastUserMessage()
    {
        var history = ThreeTurns();

        Assert.Equal(0, CompactionApplier.ToolResultsBoundary(history));   // 倒數第 3 則使用者訊息是 seq 1 → 之前沒有東西
        Assert.Equal(0, CompactionApplier.ToolResultsBoundary(history[..8]));   // 只有兩則使用者訊息
        Assert.Equal(4, CompactionApplier.SummaryBoundary(history));   // 倒數第 2 則是 seq 5
    }

    [Fact]
    public void Boundaries_AreZero_WhenTooFewUserMessages()
    {
        Assert.Equal(0, CompactionApplier.SummaryBoundary([User(1, "a"), Assistant(2, new TextBlock("b"))]));
        Assert.Equal(0, CompactionApplier.ToolResultsBoundary([User(1, "a"), Assistant(2, new TextBlock("b")), User(3, "c"), Assistant(4, new TextBlock("d"))]));
    }

    [Fact]
    public void ToolResults_UpToBoundary_BecomeOneLineAndLoseImages_PairingStaysIntact()
    {
        var history = ThreeTurns();

        var applied = CompactionApplier.Apply(history, new CompactionState(7, null, null));

        var elided = applied.First(m => m.Seq == 3).Content.OfType<ToolResultBlock>().Single();
        Assert.Equal("（較早的工具結果已省略：讀取檔案 · a.txt）", elided.Content);
        Assert.Null(elided.Images);
        Assert.Equal("u1", elided.ToolUseId);
        Assert.Contains("很長的結果", applied.First(m => m.Seq == 11).Content.OfType<ToolResultBlock>().Single().Content);   // 邊界之後不動
        Assert.NotNull(applied.First(m => m.Seq == 11).Content.OfType<ToolResultBlock>().Single().Images);

        // 成對結構照舊：每個 tool_use 後面緊接結果
        var request = ConversationService.BuildRequestMessages(applied);
        for (var i = 0; i < request.Count; i++)
        {
            foreach (var use in request[i].Content.OfType<ToolUseBlock>())
                Assert.Contains(request[i + 1].Content.OfType<ToolResultBlock>(), r => r.ToolUseId == use.Id);
        }
    }

    [Fact]
    public void ToolResults_ErrorFlagIsKept()
    {
        var history = ThreeTurns();
        history[2] = Msg(3, ChatRole.Tool, new ToolResultBlock("u1", "失敗了", true, "讀取檔案", null, "x", 1, ToolApproval.NotRequired));

        var applied = CompactionApplier.Apply(history, new CompactionState(4, null, null));

        var block = applied.First(m => m.Seq == 3).Content.OfType<ToolResultBlock>().Single();
        Assert.True(block.IsError);
        Assert.Equal("（較早的工具結果已省略：讀取檔案）", block.Content);
    }

    [Fact]
    public void Summary_ReplacesTheEarlierMessages_AndPrefixesTheFirstUserMessage()
    {
        var history = ThreeTurns();

        var applied = CompactionApplier.Apply(history, new CompactionState(null, 4, "使用者要查資料", 11));

        Assert.Equal(5, applied[0].Seq);
        var first = applied[0].Content;
        var prefix = Assert.IsType<TextBlock>(first[0]);
        Assert.StartsWith("<conversation_summary>", prefix.Text);
        Assert.Contains("這是先前對話的摘要", prefix.Text);
        Assert.Contains("使用者要查資料", prefix.Text);
        Assert.EndsWith("</conversation_summary>", prefix.Text);
        Assert.Equal("第二題", Assert.IsType<TextBlock>(first[1]).Text);
        Assert.DoesNotContain(applied, m => m.Seq <= 4);
        // 存檔的歷史不變
        Assert.Equal(11, history.Count);
    }

    [Fact]
    public void Summary_ResultIsAValidRequest_StartingWithUser()
    {
        var applied = CompactionApplier.Apply(ThreeTurns(), new CompactionState(null, 4, "摘要", 11));

        var request = ConversationService.BuildRequestMessages(applied);

        Assert.Equal(ChatRole.User, request[0].Role);
        Assert.Equal(ChatRole.Tool, request[^1].Role);   // 進行中的工具迴圈接在後面
    }

    [Fact]
    public void Thinking_OnlyKeptAfterTheLastUserMessage_WhenCompacted()
    {
        var applied = CompactionApplier.Apply(ThreeTurns(), new CompactionState(null, 4, "摘要", 11));

        Assert.DoesNotContain(applied.First(m => m.Seq == 6).Content, b => b is ThinkingBlock);
        Assert.Contains(applied.First(m => m.Seq == 10).Content, b => b is ThinkingBlock);
        // 其他內容（tool_use）還在
        Assert.Contains(applied.First(m => m.Seq == 6).Content, b => b is ToolUseBlock);
    }

    [Fact]
    public void Thinking_RedactedIsAlsoRemoved()
    {
        var history = ThreeTurns();
        history[1] = Assistant(2, new RedactedThinkingBlock("x"), new ToolUseBlock("u1", "read_file", "{}"));

        var applied = CompactionApplier.Apply(history, new CompactionState(4, null, null));

        Assert.DoesNotContain(applied.First(m => m.Seq == 2).Content, b => b is RedactedThinkingBlock);
    }

    [Fact]
    public void SummaryInput_FlattensToolsToText_EndsWithInstruction_AndStartsWithUser()
    {
        var history = ThreeTurns();

        var input = CompactionApplier.BuildSummaryInput(history, CompactionState.Empty, 8);

        Assert.Equal(ChatRole.User, input[0].Role);
        Assert.Equal(ChatRole.User, input[^1].Role);
        Assert.Equal(CompactionApplier.SummaryInstruction, Assert.IsType<TextBlock>(input[^1].Content[^1]).Text);
        Assert.All(input, m =>
        {
            Assert.NotEqual(ChatRole.Tool, m.Role);
            Assert.DoesNotContain(m.Content, b => b is ToolUseBlock or ToolResultBlock or ThinkingBlock);
        });
        // 交替
        for (var i = 1; i < input.Count; i++)
            Assert.NotEqual(input[i - 1].Role, input[i].Role);
        var text = string.Join("\n", input.SelectMany(m => m.Content.OfType<TextBlock>()).Select(t => t.Text));
        Assert.Contains("[呼叫工具 read_file]", text);
        Assert.Contains("第二題", text);
        Assert.DoesNotContain("第三題", text);   // 邊界之後不在輸入裡
    }

    [Fact]
    public void SummaryInput_IncludesThePreviousSummary()
    {
        var history = ThreeTurns();
        var state = new CompactionState(null, 4, "先前的摘要內容", 11);

        var input = CompactionApplier.BuildSummaryInput(history, state, 8);

        var first = Assert.IsType<TextBlock>(input[0].Content[0]);
        Assert.Contains("先前的摘要內容", first.Text);
        Assert.DoesNotContain(input, m => m.Content.OfType<TextBlock>().Any(t => t.Text == "第一題"));
    }

    [Fact]
    public void SummaryInput_ElidesOldToolResults_EvenWhenLayerOneWasNeverStored()
    {
        // 五輪：第 1 輪的工具結果在倒數第 3 則使用者訊息之前
        var history = new List<ChatMessage>();
        var seq = 1;
        for (var turn = 1; turn <= 5; turn++)
        {
            history.Add(User(seq++, $"題{turn}"));
            history.Add(Assistant(seq++, new ToolUseBlock($"u{turn}", "read_file", "{}")));
            history.Add(Msg(seq++, ChatRole.Tool, Result($"u{turn}", $"結果內容{turn}", $"f{turn}.txt")));
            history.Add(Assistant(seq++, new TextBlock($"答{turn}")));
        }

        var input = CompactionApplier.BuildSummaryInput(history, CompactionState.Empty, CompactionApplier.SummaryBoundary(history));

        var text = string.Join("\n", input.SelectMany(m => m.Content.OfType<TextBlock>()).Select(t => t.Text));
        Assert.DoesNotContain("結果內容1", text);
        Assert.DoesNotContain("結果內容2", text);
        Assert.Contains("較早的工具結果已省略：讀取檔案 · f1.txt", text);
        Assert.Contains("結果內容3", text);   // 摘要範圍內、但在第一層邊界之後的不省略
        Assert.DoesNotContain("題4", text);
    }

    [Fact]
    public void Estimator_UsesReportedUsage_AfterTheCompaction_ButIgnoresItBefore()
    {
        var history = ThreeTurns();
        history[7] = history[7] with { Usage = new TokenUsage(1000, 200, 300, 4000) };   // seq 8

        var fresh = ContextEstimator.Estimate(history, CompactionState.Empty, overheadTokens: 99999);
        Assert.Equal(1000 + 300 + 4000 + 200 + ContextEstimator.EstimateMessage(history[8]) + ContextEstimator.EstimateMessage(history[9]) + ContextEstimator.EstimateMessage(history[10]),
            fresh);

        // 整理發生在這則回應之後：用量作廢，改用字元估算（加上 system／工具的固定開銷）
        var stale = ContextEstimator.Estimate(history, new CompactionState(4, null, null, AppliedAtSeq: 8), overheadTokens: 500);
        Assert.Equal(500 + history.Sum(ContextEstimator.EstimateMessage), stale);
    }

    [Fact]
    public void EstimateText_CountsCjkAsOneTokenPerCharacter_AndOthersAsThirds()
    {
        Assert.Equal(0, ContextEstimator.EstimateText(null));
        Assert.Equal(10, ContextEstimator.EstimateText("一二三四五六七八九十"));
        Assert.Equal(4, ContextEstimator.EstimateText("abcdefghij"));   // 10 個字元 → ⌈10/3⌉
    }
}
