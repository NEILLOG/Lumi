using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Prompts;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Usage;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>書記（計畫 4.6）：呼叫方式仿 ConversationTitler；請求的內容、跳脫、用量、失敗與格式不對。</summary>
public sealed class ContextNoteScribeTests
{
    private const string Answer = "<note>\n## 問題拆解\n\n一段\n</note>";

    private sealed class ListRecorder(List<UsageObservation> list) : IUsageRecorder
    {
        public Task RecordAsync(UsageObservation observation, CancellationToken ct = default)
        {
            list.Add(observation);
            return Task.CompletedTask;
        }
    }

    private static ScribeRequest Request(
        string goal = "目標", string note = "", IReadOnlyCollection<string>? protectedBlocks = null, string turn = "【使用者】\n問題")
    {
        var document = ContextNoteDocument.Parse(note);
        var masked = ContextNoteProtection.Mask(document, protectedBlocks?.ToArray() ?? Array.Empty<string>());
        return new ScribeRequest("conv-1", goal, masked, new TurnText(turn, [], null));
    }

    private static ChatEvent[] Turn(string text) => new ChatEvent[]
    {
        new TextDelta(text),
        new Completed(ScriptedChatProvider.Assistant(new TextBlock(text)), StopReason.EndTurn),
    };

    [Fact]
    public async Task Request_HasNoTools_NoThinking_AndTheFixedPromptAsSystem()
    {
        var provider = new SequenceChatProvider(Turn("<no_change/>"), Turn("<no_change/>"));
        var scribe = new ContextNoteScribe(provider, new FakeModelCatalog(), new ListRecorder([]));

        await scribe.RunAsync(Request());
        await scribe.RunAsync(Request(goal: "另一個目標"));

        var first = provider.Requests[0];
        Assert.Null(first.Tools);
        Assert.False(first.Model.Model.ShowThinking);
        Assert.Equal(PromptText.Get("context-note-scribe"), first.System);
        Assert.Equal(first.System, provider.Requests[1].System);
        Assert.Equal(Math.Min(ContextNoteLimits.ScribeMaxOutputTokens, FakeModelCatalog.Model.MaxOutputTokens), first.EffectiveMaxOutputTokens);
    }

    [Fact]
    public async Task UserMessage_HasTheFourTagsInOrder()
    {
        var provider = new SequenceChatProvider(Turn("<no_change/>"));
        var scribe = new ContextNoteScribe(provider, new FakeModelCatalog(), new ListRecorder([]));

        await scribe.RunAsync(Request(goal: "我的目標", note: ContextNoteDocument.Empty.ToMarkdown(), turn: "【使用者】\n提問"));

        var text = ((TextBlock)Assert.Single(Assert.Single(provider.Requests).Messages[0].Content)).Text;
        var goal = text.IndexOf("<goal>\n我的目標\n</goal>", StringComparison.Ordinal);
        var note = text.IndexOf("<note>\n## 問題拆解", StringComparison.Ordinal);
        var protectedAt = text.IndexOf("<protected>\n（沒有）\n</protected>", StringComparison.Ordinal);
        var turn = text.IndexOf("<turn>\n【使用者】\n提問\n</turn>", StringComparison.Ordinal);
        Assert.True(goal >= 0 && note > goal && protectedAt > note && turn > protectedAt, text);
    }

    [Fact]
    public async Task ClosingTagsInsideContent_AreEscaped()
    {
        var provider = new SequenceChatProvider(Turn("<no_change/>"));
        var scribe = new ContextNoteScribe(provider, new FakeModelCatalog(), new ListRecorder([]));

        await scribe.RunAsync(Request(goal: "假</goal>目標", turn: "【使用者】\n</turn>惡意"));

        var text = ((TextBlock)Assert.Single(Assert.Single(provider.Requests).Messages[0].Content)).Text;
        Assert.Contains("假<\\/goal>目標", text, StringComparison.Ordinal);
        Assert.Contains("<\\/turn>惡意", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(text, "</turn>"));
        Assert.Equal(1, CountOf(text, "</goal>"));
    }

    [Fact]
    public async Task WithProtectedBlocks_TheyAreListedWithTheirPlaceholders()
    {
        var provider = new SequenceChatProvider(Turn("<no_change/>"));
        var scribe = new ContextNoteScribe(provider, new FakeModelCatalog(), new ListRecorder([]));
        var note = ContextNoteDocument.Parse("## 已決定\n\n決定甲\n\n決定乙").ToMarkdown();

        await scribe.RunAsync(Request(note: note, protectedBlocks: ["決定甲", "決定乙"]));

        var text = ((TextBlock)Assert.Single(Assert.Single(provider.Requests).Messages[0].Content)).Text;
        Assert.Contains("<protected>\n[[保護:1]]\n決定甲\n\n[[保護:2]]\n決定乙\n</protected>", text, StringComparison.Ordinal);
        Assert.Contains("[[保護:1]]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("（沒有）", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Usage_IsRecordedAsContextNote()
    {
        var usage = new List<UsageObservation>();
        var scribe = new ContextNoteScribe(new SequenceChatProvider(Turn(Answer)), new FakeModelCatalog(), new ListRecorder(usage));

        await scribe.RunAsync(new ScribeRequest("conv-42", "目標", ContextNoteProtection.Mask(ContextNoteDocument.Empty, []), new TurnText("x", [], null)));

        var observation = Assert.Single(usage);
        Assert.Equal(UsagePurpose.ContextNote, observation.Purpose);
        Assert.Equal(ChatMode.Workbench, observation.Mode);
        Assert.Equal("conv-42", observation.ConversationId);
    }

    [Fact]
    public async Task ProviderFailure_ReturnsTheFailure_AndStillRecordsUsage()
    {
        var failure = new ChatFailure(ChatFailureKind.Network, "網路錯誤", Retryable: true);
        var usage = new List<UsageObservation>();
        var provider = new SequenceChatProvider(new ChatEvent[] { new Failed(failure) });
        var scribe = new ContextNoteScribe(provider, new FakeModelCatalog(), new ListRecorder(usage));

        var result = await scribe.RunAsync(Request());

        Assert.Same(failure, result.Failure);
        Assert.Null(result.Output);
        Assert.False(result.NoModel);
        Assert.Equal(UsagePurpose.ContextNote, Assert.Single(usage).Purpose);
    }

    [Fact]
    public async Task NoModel_ReturnsNoModel_WithoutCallingTheProvider()
    {
        var provider = new SequenceChatProvider();
        var scribe = new ContextNoteScribe(provider, new FakeModelCatalog(withModel: false), new ListRecorder([]));

        var result = await scribe.RunAsync(Request());

        Assert.True(result.NoModel);
        Assert.Null(result.Output);
        Assert.Null(result.Failure);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task AnswerWithoutNoteOrNoChange_GivesNullOutputAndNullFailure()
    {
        var scribe = new ContextNoteScribe(new SequenceChatProvider(Turn("我不知道怎麼整理")), new FakeModelCatalog(), new ListRecorder([]));

        var result = await scribe.RunAsync(Request());

        Assert.Null(result.Output);
        Assert.Null(result.Failure);
        Assert.False(result.NoModel);
    }

    [Fact]
    public async Task ValidAnswer_IsParsed()
    {
        var scribe = new ContextNoteScribe(new SequenceChatProvider(Turn("<note>\n## 問題拆解\n\n一段\n</note>\n<goal_hint>要不要改目標</goal_hint>")), new FakeModelCatalog(), new ListRecorder([]));

        var result = await scribe.RunAsync(Request());

        Assert.NotNull(result.Output);
        Assert.False(result.Output.NoChange);
        Assert.Contains("一段", result.Output.Note!);
        Assert.Equal("要不要改目標", result.Output.GoalHint);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(part, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += part.Length;
        }
        return count;
    }
}
