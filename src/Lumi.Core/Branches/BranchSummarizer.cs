using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Branches;

/// <summary>
/// 帶回時整理支線摘要的那一次 AI 呼叫（計畫 4.5，仿 <see cref="ContextNoteScribe"/>）。
/// 不帶工具、不要求 thinking、system 逐字固定（<c>branch-summary</c> 的 prompt），會變的內容都在使用者訊息。
/// 用量記在主線（<see cref="BranchSummaryRequest.RootId"/>）、用途 <see cref="UsagePurpose.BranchSummary"/>；成功、失敗、被取消都記。
/// </summary>
public sealed class BranchSummarizer(IChatProvider provider, IModelCatalog catalog, IUsageRecorder? usage = null, ILogger<BranchSummarizer>? logger = null) : IBranchSummarizer
{
    /// <summary>使用者訊息裡會被跳脫的結尾標籤（6.4）；內容裡出現時把開頭的 <c>&lt;</c> 換成 <c>&amp;lt;</c>，避免提前結束。</summary>
    private static readonly string[] ClosingTags =
        ["</main_context", "</fork_answer", "</selected", "</main_question", "</branch_transcript", "</child_summaries", "</summary"];

    private readonly IUsageRecorder _usage = usage ?? new NullUsageRecorder();
    private readonly ILogger<BranchSummarizer> _logger = logger ?? NullLogger<BranchSummarizer>.Instance;

    public async Task<BranchSummaryDraft> SummarizeAsync(BranchSummaryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (catalog.Resolve(catalog.Current.WorkbenchModelId) is not { } resolved)
            return new BranchSummaryDraft(null, null, NoModel: true);

        var model = new ResolvedModel(resolved.Model with { ShowThinking = false }, resolved.Connection);
        var chatRequest = new ChatRequest(
            model,
            [BuildMessage(request)],
            Tools: null,
            System: PromptText.Get("branch-summary"),
            TurnSuffix: null,
            MaxOutputTokens: Math.Min(BranchLimits.SummarizerMaxOutputTokens, resolved.Model.MaxOutputTokens));

        var text = new StringBuilder();
        Completed? completed = null;
        TokenUsage? lastUsage = null;
        try
        {
            await foreach (var chatEvent in provider.StreamAsync(chatRequest, ct).ConfigureAwait(false))
            {
                switch (chatEvent)
                {
                    case TextDelta delta:
                        text.Append(delta.Text);
                        break;
                    case UsageEvent u:
                        lastUsage = u.Tokens;
                        break;
                    case Completed c:
                        completed = c;
                        break;
                    case Failed f:
                        _logger.LogInformation("整理支線摘要失敗（{Kind}）。", f.Failure.Kind);
                        await RecordAsync(request.RootId, model, chatRequest, lastUsage, text.ToString(), estimatedOnly: true).ConfigureAwait(false);
                        return new BranchSummaryDraft(null, f.Failure);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordAsync(request.RootId, model, chatRequest, lastUsage, text.ToString(), estimatedOnly: true).ConfigureAwait(false);
            throw;
        }

        await RecordAsync(request.RootId, model, chatRequest, completed?.Message.Usage ?? lastUsage, text.ToString(), estimatedOnly: false).ConfigureAwait(false);

        var answer = completed is null
            ? text.ToString()
            : string.Concat(completed.Message.Content.OfType<TextBlock>().Select(t => t.Text));
        var summary = answer.Trim();
        if (summary.Length == 0)
            return new BranchSummaryDraft(null, new ChatFailure(ChatFailureKind.Unknown, BranchMessages.SummaryEmptyMessage, Retryable: true));
        return new BranchSummaryDraft(summary, null);
    }

    /// <summary>組使用者訊息（計畫 6.4）。沒有的部分整段省略。</summary>
    private static ChatMessage BuildMessage(BranchSummaryRequest request)
    {
        var sections = new List<string>();

        var mainContext = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.ForkText))
            mainContext.Add("<fork_answer>\n" + Escape(request.ForkText) + "\n</fork_answer>");
        if (!string.IsNullOrWhiteSpace(request.Quote))
            mainContext.Add("<selected>" + Escape(request.Quote) + "</selected>");
        if (!string.IsNullOrWhiteSpace(request.MainLastUserText))
            mainContext.Add("<main_question>\n" + Escape(request.MainLastUserText) + "\n</main_question>");
        if (mainContext.Count > 0)
            sections.Add("<main_context>\n" + string.Join("\n", mainContext) + "\n</main_context>");

        var transcript = ContextNoteTurnFormatter.Format(request.BranchMessages, BranchLimits.TranscriptMaxChars).Text;
        sections.Add("<branch_transcript>\n" + Escape(transcript) + "\n</branch_transcript>");

        if (request.ChildSummaries.Count > 0)
        {
            var children = request.ChildSummaries.Select(c =>
                $"<summary title=\"{EscapeTitle(c.Title)}\">\n{Escape(c.Summary)}\n</summary>");
            sections.Add("<child_summaries>\n" + string.Join("\n", children) + "\n</child_summaries>");
        }

        var text = string.Join("\n\n", sections);
        return new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);
    }

    private static string Escape(string text)
    {
        foreach (var tag in ClosingTags)
            text = text.Replace(tag, "&lt;" + tag[1..], StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>標題放在屬性值的引號裡：&amp;、引號、&lt; 都跳脫。</summary>
    private static string EscapeTitle(string title) =>
        title.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal);

    /// <summary>記用量（失敗不影響整理）。沒有供應商回報的 usage 時用估算值。</summary>
    private async Task RecordAsync(
        string rootId, ResolvedModel model, ChatRequest request, TokenUsage? reported, string output, bool estimatedOnly)
    {
        var estimated = estimatedOnly || reported is null;
        var tokens = !estimatedOnly && reported is { } r
            ? r
            : new TokenUsage(ConversationService.EstimateRequestInput(request), ContextEstimator.EstimateText(output), 0, 0);
        try
        {
            await _usage.RecordAsync(
                    new UsageObservation(rootId, UsagePurpose.BranchSummary, ChatMode.Workbench, model, tokens, estimated, 0),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "記錄整理支線摘要的用量失敗。");
        }
    }
}
