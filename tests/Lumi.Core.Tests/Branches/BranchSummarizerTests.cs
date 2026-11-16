using System.Runtime.CompilerServices;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Prompts;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Branches;

/// <summary>整理摘要（計畫 4.5、6.4）：呼叫方式仿 ContextNoteScribe；請求、跳脫、用量、失敗與取消。</summary>
public sealed class BranchSummarizerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private sealed class ListRecorder(List<UsageObservation> list) : IUsageRecorder
    {
        public Task RecordAsync(UsageObservation observation, CancellationToken ct = default)
        {
            list.Add(observation);
            return Task.CompletedTask;
        }
    }

    /// <summary>先送出一段文字，然後取消呼叫方的 token 並一直等待，模擬使用者中途取消。</summary>
    private sealed class CancelAfterDeltaProvider(CancellationTokenSource cts) : IChatProvider
    {
        public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TextDelta("部分");
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    private static ChatMessage Msg(int seq, ChatRole role, string text) =>
        new(ChatMessage.NewId(), role, [new TextBlock(text)], At) { Seq = seq };

    private static BranchSummaryRequest Request(
        string fork = "分岔的回答",
        string? quote = null,
        string? mainQuestion = null,
        IReadOnlyList<ChatMessage>? branch = null,
        IReadOnlyList<(string Title, string Summary)>? children = null) =>
        new("root-1", fork, quote, mainQuestion, branch ?? [Msg(1, ChatRole.User, "支線問題")], children ?? []);

    private static string UserText(ChatRequest request) =>
        ((TextBlock)Assert.Single(Assert.Single(request.Messages).Content)).Text;

    private static ChatEvent[] Turn(string text) =>
    [
        new TextDelta(text),
        new Completed(ScriptedChatProvider.Assistant(new TextBlock(text)), StopReason.EndTurn),
    ];

    [Fact]
    public async Task No_model_does_not_call_provider()
    {
        var provider = new SequenceChatProvider();
        var summarizer = new BranchSummarizer(provider, new FakeModelCatalog(withModel: false), new ListRecorder([]));

        var draft = await summarizer.SummarizeAsync(Request());

        Assert.True(draft.NoModel);
        Assert.Null(draft.Text);
        Assert.Null(draft.Failure);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Request_has_fixed_system_no_tools_no_thinking()
    {
        var provider = new SequenceChatProvider(Turn("摘要"));
        var summarizer = new BranchSummarizer(provider, new FakeModelCatalog(), new ListRecorder([]));

        await summarizer.SummarizeAsync(Request());

        var request = Assert.Single(provider.Requests);
        Assert.Null(request.Tools);
        Assert.False(request.Model.Model.ShowThinking);
        Assert.Equal(PromptText.Get("branch-summary"), request.System);
        Assert.Equal(Math.Min(BranchLimits.SummarizerMaxOutputTokens, FakeModelCatalog.Model.MaxOutputTokens), request.EffectiveMaxOutputTokens);
    }

    [Fact]
    public async Task User_message_contains_sections_in_order()
    {
        var provider = new SequenceChatProvider(Turn("摘要"));
        var summarizer = new BranchSummarizer(provider, new FakeModelCatalog(), new ListRecorder([]));

        await summarizer.SummarizeAsync(Request(
            fork: "分岔文字",
            quote: "選取的字",
            mainQuestion: "主線問題",
            branch: [Msg(1, ChatRole.User, "支線問題甲")],
            children: [("子支線", "子摘要內容")]));

        var text = UserText(Assert.Single(provider.Requests));
        var expected = """
            <main_context>
            <fork_answer>
            分岔文字
            </fork_answer>
            <selected>選取的字</selected>
            <main_question>
            主線問題
            </main_question>
            </main_context>

            <branch_transcript>
            【使用者】
            支線問題甲
            </branch_transcript>

            <child_summaries>
            <summary title="子支線">
            子摘要內容
            </summary>
            </child_summaries>
            """.Replace("\r\n", "\n");
        Assert.Equal(expected, text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Optional_sections_are_omitted()
    {
        var provider = new SequenceChatProvider(Turn("摘要"));
        var summarizer = new BranchSummarizer(provider, new FakeModelCatalog(), new ListRecorder([]));

        await summarizer.SummarizeAsync(Request(fork: "", quote: null, mainQuestion: " ", children: []));

        var text = UserText(Assert.Single(provider.Requests));
        Assert.DoesNotContain("<main_context>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<fork_answer>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<selected>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<main_question>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<child_summaries>", text, StringComparison.Ordinal);
        Assert.StartsWith("<branch_transcript>", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_tags_are_escaped()
    {
        var provider = new SequenceChatProvider(Turn("摘要"));
        var summarizer = new BranchSummarizer(provider, new FakeModelCatalog(), new ListRecorder([]));

        await summarizer.SummarizeAsync(Request(
            fork: "假</fork_answer>回答",
            quote: "假</selected>字",
            mainQuestion: "假</main_question>問",
            branch: [Msg(1, ChatRole.User, "假</branch_transcript>話")],
            children: [("標題", "假</summary>摘要")]));

        var text = UserText(Assert.Single(provider.Requests));
        Assert.Contains("&lt;/fork_answer>回答", text, StringComparison.Ordinal);
        Assert.Contains("&lt;/selected>字", text, StringComparison.Ordinal);
        Assert.Contains("&lt;/main_question>問", text, StringComparison.Ordinal);
        Assert.Contains("&lt;/branch_transcript>話", text, StringComparison.Ordinal);
        Assert.Contains("&lt;/summary>摘要", text, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(text, "</fork_answer>"));
        Assert.Equal(1, CountOf(text, "</selected>"));
        Assert.Equal(1, CountOf(text, "</main_question>"));
        Assert.Equal(1, CountOf(text, "</branch_transcript>"));
        Assert.Equal(1, CountOf(text, "</summary>"));
    }

    [Fact]
    public async Task Transcript_is_truncated_to_limit()
    {
        var provider = new SequenceChatProvider(Turn("摘要"));
        var summarizer = new BranchSummarizer(provider, new FakeModelCatalog(), new ListRecorder([]));
        var filler = new string('字', 5_000);
        var branch = Enumerable.Range(1, 10)
            .Select(i => Msg(i, ChatRole.User, $"第{i:D2}則標記 {filler}"))
            .ToList();

        await summarizer.SummarizeAsync(Request(branch: branch));

        var text = UserText(Assert.Single(provider.Requests));
        Assert.Contains("（較早的對話已省略）", text, StringComparison.Ordinal);
        Assert.DoesNotContain("第01則標記", text, StringComparison.Ordinal);
        Assert.Contains("第10則標記", text, StringComparison.Ordinal);
        Assert.True(text.Length < BranchLimits.TranscriptMaxChars + 2_000, $"文字長度 {text.Length} 超過預期");
    }

    [Fact]
    public async Task Returns_trimmed_text()
    {
        var summarizer = new BranchSummarizer(new SequenceChatProvider(Turn("  \n摘要內容\n\n")), new FakeModelCatalog(), new ListRecorder([]));

        var draft = await summarizer.SummarizeAsync(Request());

        Assert.Equal("摘要內容", draft.Text);
        Assert.Null(draft.Failure);
        Assert.False(draft.NoModel);
    }

    [Fact]
    public async Task Empty_text_is_failure()
    {
        var summarizer = new BranchSummarizer(new SequenceChatProvider(Turn("   \n ")), new FakeModelCatalog(), new ListRecorder([]));

        var draft = await summarizer.SummarizeAsync(Request());

        Assert.Null(draft.Text);
        Assert.NotNull(draft.Failure);
        Assert.Equal(BranchMessages.SummaryEmptyMessage, draft.Failure.Message);
        Assert.True(draft.Failure.Retryable);
    }

    [Fact]
    public async Task Provider_failure_is_returned()
    {
        var failure = new ChatFailure(ChatFailureKind.Network, "網路錯誤", Retryable: true);
        var usage = new List<UsageObservation>();
        var summarizer = new BranchSummarizer(new SequenceChatProvider(new ChatEvent[] { new Failed(failure) }), new FakeModelCatalog(), new ListRecorder(usage));

        var draft = await summarizer.SummarizeAsync(Request());

        Assert.Same(failure, draft.Failure);
        Assert.Null(draft.Text);
        Assert.False(draft.NoModel);
        Assert.Equal(UsagePurpose.BranchSummary, Assert.Single(usage).Purpose);
    }

    [Fact]
    public async Task Usage_is_recorded_on_root_with_branch_summary_purpose()
    {
        var usage = new List<UsageObservation>();
        var summarizer = new BranchSummarizer(new SequenceChatProvider(Turn("摘要")), new FakeModelCatalog(), new ListRecorder(usage));

        await summarizer.SummarizeAsync(Request());

        var observation = Assert.Single(usage);
        Assert.Equal("root-1", observation.ConversationId);
        Assert.Equal(UsagePurpose.BranchSummary, observation.Purpose);
        Assert.Equal(ChatMode.Workbench, observation.Mode);
    }

    [Fact]
    public async Task Cancellation_records_usage_and_throws()
    {
        using var cts = new CancellationTokenSource();
        var usage = new List<UsageObservation>();
        var summarizer = new BranchSummarizer(new CancelAfterDeltaProvider(cts), new FakeModelCatalog(), new ListRecorder(usage));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => summarizer.SummarizeAsync(Request(), cts.Token));

        var observation = Assert.Single(usage);
        Assert.Equal(UsagePurpose.BranchSummary, observation.Purpose);
        Assert.Equal("root-1", observation.ConversationId);
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
