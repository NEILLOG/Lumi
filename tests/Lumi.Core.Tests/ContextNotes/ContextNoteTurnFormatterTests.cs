using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Context;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35C：書記看的對話文字（計畫 4.6 的表與要整理哪些訊息）。</summary>
public class ContextNoteTurnFormatterTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static ChatMessage Msg(int seq, ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, At) { Seq = seq };

    private static ChatMessage Msg(int seq, string id, ChatRole role, params ContentBlock[] content) =>
        new(id, role, content, At) { Seq = seq };

    private static ToolResultBlock Result(string toolUseId, string content, string displayName = "搜尋網頁", string? target = null, bool isError = false,
        IReadOnlyList<ImageBlock>? images = null, IReadOnlyList<string>? imageFiles = null) =>
        new(toolUseId, content, isError, displayName, target, null, 0, ToolApproval.NotRequired, images, imageFiles);

    [Fact]
    public void UserText_BecomesUserHeaderAndText()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.User, new TextBlock("你好"))]);

        Assert.Equal("【使用者】\n你好", turn.Text);
        Assert.Empty(turn.Images);
    }

    [Fact]
    public void AssistantText_BecomesLumiHeaderAndText()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.Assistant, new TextBlock("回答"))]);

        Assert.Equal("【Lumi】\n回答", turn.Text);
    }

    [Fact]
    public void ThinkingAndProviderBlocks_AreSkipped()
    {
        var turn = ContextNoteTurnFormatter.Format(
        [
            Msg(1, ChatRole.Assistant, new ThinkingBlock("想一想", "sig"), new ProviderBlock("anthropic", "fallback", "{}"), new TextBlock("答")),
            Msg(2, ChatRole.Assistant, new ThinkingBlock("只有思考", "sig")),
        ]);

        Assert.Equal("【Lumi】\n答", turn.Text);
    }

    [Fact]
    public void ToolUseBlock_BecomesNameWithoutArguments()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.Assistant, new ToolUseBlock("t1", "web_search", "{\"q\":\"秘密參數\"}"))]);

        Assert.Equal("【Lumi】\n[呼叫工具：web_search]", turn.Text);
    }

    [Fact]
    public void ToolResult_BecomesDisplayNameTargetAndContent()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.Tool, Result("t1", "結果內容", "搜尋網頁", "lumi"))]);

        Assert.Equal("【工具】\n[工具結果：搜尋網頁 · lumi]\n結果內容", turn.Text);
    }

    [Fact]
    public void ToolResult_FailedWithoutTarget_OmitsTargetAndSaysFailed()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.Tool, Result("t1", "錯誤內容", "搜尋網頁", target: null, isError: true))]);

        Assert.Equal("【工具】\n[工具結果：搜尋網頁（失敗）]\n錯誤內容", turn.Text);
    }

    [Fact]
    public void ToolResult_WithoutDisplayName_UsesToolUseName_ThenFallsBackToTool()
    {
        var fromUse = ContextNoteTurnFormatter.Format(
        [
            Msg(1, ChatRole.Assistant, new ToolUseBlock("t1", "read_file", "{}")),
            Msg(2, ChatRole.Tool, Result("t1", "內容", displayName: "")),
        ]);
        var unknown = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.Tool, Result("zz", "內容", displayName: ""))]);

        Assert.Contains("[工具結果：read_file]", fromUse.Text);
        Assert.Contains("[工具結果：工具]", unknown.Text);
    }

    [Fact]
    public void ToolResult_ContentIsCutAt600Characters()
    {
        var long600 = new string('a', 600);

        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.Tool, Result("t1", long600 + new string('b', 100)))]);

        Assert.Equal("【工具】\n[工具結果：搜尋網頁]\n" + long600, turn.Text);
    }

    [Fact]
    public void ContextFile_BecomesNameWithoutContent()
    {
        var turn = ContextNoteTurnFormatter.Format(
            [Msg(1, ChatRole.User, new TextBlock("看這個"), new ContextBlock(ContextKind.File, "報告.pdf", "/路徑/報告.pdf", "檔案全文不該出現", true))]);

        Assert.Equal("【使用者】\n看這個\n[附加檔案：報告.pdf]", turn.Text);
    }

    [Fact]
    public void ContextFolder_BecomesNameWithoutContent()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.User, new ContextBlock(ContextKind.Folder, "專案", "/路徑/專案", null, false, "太大"))]);

        Assert.Equal("【使用者】\n[附加檔案：專案]", turn.Text);
    }

    [Fact]
    public void Scribe_sees_branch_summary()
    {
        var turn = ContextNoteTurnFormatter.Format(
            [Msg(1, ChatRole.User, new TextBlock("帶回來吧"), new ContextBlock(ContextKind.BranchSummary, "比較方案", "b1", "摘要第一行\n摘要第二行", true))]);

        Assert.Equal("【使用者】\n帶回來吧\n[帶回的支線：比較方案]\n摘要第一行\n摘要第二行", turn.Text);
    }

    [Fact]
    public void BranchOrigin_is_not_written()
    {
        var turn = ContextNoteTurnFormatter.Format(
            [Msg(1, ChatRole.User, new ContextBlock(ContextKind.BranchOrigin, "branch_origin", "m1", "<branch_start>出處全文</branch_start>", true), new TextBlock("正文"))]);

        Assert.Equal("【使用者】\n正文", turn.Text);
    }

    [Fact]
    public void File_context_is_unchanged_next_to_branch_blocks()
    {
        var turn = ContextNoteTurnFormatter.Format(
            [Msg(1, ChatRole.User, new ContextBlock(ContextKind.File, "報告.pdf", "/路徑/報告.pdf", "全文", true), new ContextBlock(ContextKind.BranchSummary, "方案", "b1", "摘要", true))]);

        Assert.Equal("【使用者】\n[附加檔案：報告.pdf]\n[帶回的支線：方案]\n摘要", turn.Text);
    }

    [Fact]
    public void ScheduledRunAndRecentConversations_AreSkipped()
    {
        var turn = ContextNoteTurnFormatter.Format(
        [
            Msg(1, ChatRole.User, new ContextBlock(ContextKind.ScheduledRun, "排程", "", null, false)),
            Msg(2, ChatRole.User, new ContextBlock(ContextKind.RecentConversations, "最近", "", null, false), new TextBlock("正文")),
        ]);

        Assert.Equal("【使用者】\n正文", turn.Text);
    }

    [Fact]
    public void Images_AreNumberedAcrossTheBatchAndMappedToTheirSources()
    {
        var turn = ContextNoteTurnFormatter.Format(
        [
            Msg(1, ChatRole.User, new ImageBlock("abc.png", "image/png", "截圖")),
            Msg(2, ChatRole.Tool, Result("t1", "結果", images: [new ImageBlock("f1.jpg", "image/jpeg", "圖甲")], imageFiles: ["/tmp/x/shot.webp"])),
        ]);

        Assert.Equal(
            "【使用者】\n[圖片 圖1：截圖]\n\n【工具】\n[工具結果：搜尋網頁]\n結果\n[圖片 圖2：圖甲]\n[圖片 圖3：shot.webp]",
            turn.Text);
        Assert.Equal(
            [
                new TurnImage("圖1", "abc.png", null, "image/png", "截圖"),
                new TurnImage("圖2", "f1.jpg", null, "image/jpeg", "圖甲"),
                new TurnImage("圖3", null, "/tmp/x/shot.webp", "image/webp", "shot.webp"),
            ],
            turn.Images);
    }

    [Fact]
    public void LongText_KeepsHeadAndTailWithOmissionLine()
    {
        var head = new string('h', ContextNoteLimits.TurnTextHead);
        var middle = new string('m', 1000);
        var tail = new string('t', ContextNoteLimits.TurnTextTail);

        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.User, new TextBlock(head + middle + tail))]);

        Assert.Equal("【使用者】\n" + head + "\n……（中間省略）……\n" + tail, turn.Text);
    }

    [Fact]
    public void TextAtTheLimit_IsNotCut()
    {
        var text = new string('x', ContextNoteLimits.TurnTextMaxChars);

        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.User, new TextBlock(text))]);

        Assert.Equal("【使用者】\n" + text, turn.Text);
    }

    [Fact]
    public void MoreThan40Messages_OnlyTheLast40AreFormatted()
    {
        var messages = Enumerable.Range(1, 45).Select(i => Msg(i, ChatRole.User, new TextBlock($"第{i}則"))).ToList();

        var turn = ContextNoteTurnFormatter.Format(messages);

        Assert.Equal(ContextNoteLimits.TurnMaxMessages, turn.Text.Split("【使用者】").Length - 1);
        Assert.DoesNotContain("第5則", turn.Text);
        Assert.Contains("第6則", turn.Text);
        Assert.Contains("第45則", turn.Text);
    }

    [Fact]
    public void LastUserExcerpt_IsTheFirst30CharactersWithNewlinesAsSpaces()
    {
        var turn = ContextNoteTurnFormatter.Format(
            [Msg(1, ChatRole.User, new TextBlock("問題一\n問題二" + new string('字', 40)))]);

        Assert.Equal("問題一 問題二" + new string('字', 23), turn.LastUserExcerpt);
    }

    [Fact]
    public void LastUserExcerpt_IsTheLastUserMessageWithText()
    {
        var turn = ContextNoteTurnFormatter.Format(
        [
            Msg(1, ChatRole.User, new TextBlock("最後的問題")),
            Msg(2, ChatRole.Assistant, new TextBlock("回答")),
            Msg(3, ChatRole.Tool, Result("t1", "結果")),
        ]);

        Assert.Equal("最後的問題", turn.LastUserExcerpt);
    }

    [Fact]
    public void LastUserExcerpt_IsNullWithoutUserText()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.Assistant, new TextBlock("回答"))]);

        Assert.Null(turn.LastUserExcerpt);
    }

    [Fact]
    public void MaxTotalChars_CutsFromALineStartAndAddsNotice()
    {
        var messages = new[] { "甲甲甲", "乙乙乙", "丙丙丙" }
            .Select((text, i) => Msg(i + 1, ChatRole.User, new TextBlock(text)))
            .ToList();

        // 完整文字 31 字：「【使用者】\n甲甲甲\n\n【使用者】\n乙乙乙\n\n【使用者】\n丙丙丙」；只留最後 15 字，從下一行開頭開始
        var turn = ContextNoteTurnFormatter.Format(messages, maxTotalChars: 15);

        Assert.Equal("（較早的對話已省略）\n乙乙乙\n\n【使用者】\n丙丙丙", turn.Text);
    }

    [Fact]
    public void MaxTotalChars_LargerThanText_KeepsEverything()
    {
        var turn = ContextNoteTurnFormatter.Format([Msg(1, ChatRole.User, new TextBlock("短"))], maxTotalChars: 1000);

        Assert.Equal("【使用者】\n短", turn.Text);
    }

    [Fact]
    public void MaxTotalChars_DropsImagesThatWereCut()
    {
        var messages = new[]
        {
            Msg(1, ChatRole.User, new ImageBlock("a.png", "image/png", "舊圖")),
            Msg(2, ChatRole.User, new TextBlock(new string('字', 50))),
            Msg(3, ChatRole.User, new ImageBlock("b.png", "image/png", "新圖")),
        };

        var turn = ContextNoteTurnFormatter.Format(messages, maxTotalChars: 20);

        Assert.DoesNotContain("舊圖", turn.Text);
        Assert.Contains("新圖", turn.Text);
        Assert.Equal(["圖2"], turn.Images.Select(i => i.Token).ToArray());
    }

    [Fact]
    public void SelectPending_Normal_TakesMessagesAfterTheProgress()
    {
        var messages = Conversation();

        var pending = ContextNoteTurnFormatter.SelectPending(messages, 2, "m2");

        Assert.Equal([3, 4, 5], pending.Select(m => m.Seq).ToArray());
    }

    [Fact]
    public void SelectPending_ProgressAtZero_TakesEverything()
    {
        var pending = ContextNoteTurnFormatter.SelectPending(Conversation(), 0, null);

        Assert.Equal([1, 2, 3, 4, 5], pending.Select(m => m.Seq).ToArray());
    }

    [Fact]
    public void SelectPending_TailRegenerated_SameSeqDifferentId_StartsAtLastUserBeforeProgress()
    {
        // 第 4 則被重新產生：進度記的是 old4，現在第 4 則的 id 是 m4
        var pending = ContextNoteTurnFormatter.SelectPending(Conversation(), 4, "old4");

        Assert.Equal([3, 4, 5], pending.Select(m => m.Seq).ToArray());
    }

    [Fact]
    public void SelectPending_ProgressBeyondLastMessage_StartsAtLastUserBeforeProgress()
    {
        var pending = ContextNoteTurnFormatter.SelectPending(Conversation(), 7, "gone");

        Assert.Equal([5], pending.Select(m => m.Seq).ToArray());
    }

    [Fact]
    public void SelectPending_FirstPendingNotUser_BacksUpToNearestUser()
    {
        // 進度在第 1 則（使用者）之後，第一則待整理的是助理訊息 → 往前補到第 1 則
        var pending = ContextNoteTurnFormatter.SelectPending(Conversation(), 1, "m1");

        Assert.Equal([1, 2, 3, 4, 5], pending.Select(m => m.Seq).ToArray());
    }

    [Fact]
    public void SelectPending_NoNewMessages_IsEmpty()
    {
        var pending = ContextNoteTurnFormatter.SelectPending(Conversation(), 5, "m5");

        Assert.Empty(pending);
    }

    private static List<ChatMessage> Conversation() =>
    [
        Msg(1, "m1", ChatRole.User, new TextBlock("a")),
        Msg(2, "m2", ChatRole.Assistant, new TextBlock("b")),
        Msg(3, "m3", ChatRole.User, new TextBlock("c")),
        Msg(4, "m4", ChatRole.Assistant, new TextBlock("d")),
        Msg(5, "m5", ChatRole.User, new TextBlock("e")),
    ];
}
