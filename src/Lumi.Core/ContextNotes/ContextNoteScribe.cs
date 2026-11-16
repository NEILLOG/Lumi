using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.ContextNotes;

/// <summary>
/// 書記（計畫 4.6）：用目前工作台的模型，把一批對話整理進研究本。呼叫方式仿 <see cref="ConversationTitler"/>：
/// 不帶工具、不要求 thinking、system 逐字固定（<c>context-note-scribe</c> 的 prompt），會變的內容都在使用者訊息。
/// 用量記成 <see cref="UsagePurpose.ContextNote"/>；成功、失敗、被取消都記。
/// </summary>
public sealed class ContextNoteScribe(
    IChatProvider provider,
    IModelCatalog catalog,
    IUsageRecorder? usage = null,
    ILogger<ContextNoteScribe>? logger = null) : IContextNoteScribe
{
    /// <summary>使用者訊息裡四種內容的結尾標籤；內容裡出現時跳脫，避免提前結束。</summary>
    private static readonly string[] ClosingTags = ["</goal", "</note", "</protected", "</turn"];

    private const string NoProtected = "（沒有）";

    private readonly IUsageRecorder _usage = usage ?? new NullUsageRecorder();
    private readonly ILogger<ContextNoteScribe> _logger = logger ?? NullLogger<ContextNoteScribe>.Instance;

    public async Task<ScribeResult> RunAsync(ScribeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (catalog.Resolve(catalog.Current.WorkbenchModelId) is not { } resolved)
            return new ScribeResult(null, null, NoModel: true);

        var model = new ResolvedModel(resolved.Model with { ShowThinking = false }, resolved.Connection);
        var chatRequest = new ChatRequest(
            model,
            [BuildMessage(request)],
            Tools: null,
            System: PromptText.Get("context-note-scribe"),
            TurnSuffix: null,
            MaxOutputTokens: Math.Min(ContextNoteLimits.ScribeMaxOutputTokens, resolved.Model.MaxOutputTokens));

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
                        _logger.LogInformation("整理研究本失敗（{Kind}）。", f.Failure.Kind);
                        await RecordAsync(request.ConversationId, model, chatRequest, lastUsage, text.ToString(), estimatedOnly: true).ConfigureAwait(false);
                        return new ScribeResult(null, f.Failure);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await RecordAsync(request.ConversationId, model, chatRequest, lastUsage, text.ToString(), estimatedOnly: true).ConfigureAwait(false);
            throw;
        }

        await RecordAsync(request.ConversationId, model, chatRequest, completed?.Message.Usage ?? lastUsage, text.ToString(), estimatedOnly: false).ConfigureAwait(false);

        var answer = completed is null
            ? text.ToString()
            : string.Concat(completed.Message.Content.OfType<TextBlock>().Select(t => t.Text));
        // 格式不對回 null；Output 與 Failure 都是 null 時，服務當成「檢查不過」處理
        return new ScribeResult(ScribeOutputParser.Parse(answer), null);
    }

    /// <summary>組使用者訊息（計畫 4.6）。</summary>
    private static ChatMessage BuildMessage(ScribeRequest request)
    {
        var protectedBody = request.Masked.Entries.Count == 0
            ? NoProtected
            : string.Join("\n\n", request.Masked.Entries.Select(e => $"{ContextNoteProtection.Placeholder(e.Index)}\n{e.Text}"));

        var text = "<goal>\n" + Escape(request.Goal) + "\n</goal>\n"
                   + "<note>\n" + Escape(request.Masked.Markdown) + "\n</note>\n"
                   + "<protected>\n" + Escape(protectedBody) + "\n</protected>\n"
                   + "<turn>\n" + Escape(request.Turn.Text) + "\n</turn>";
        return new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);
    }

    private static string Escape(string text)
    {
        foreach (var tag in ClosingTags)
            text = text.Replace(tag, "<\\" + tag[1..], StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>記用量（失敗不影響整理）。沒有供應商回報的 usage 時用估算值。</summary>
    private async Task RecordAsync(
        string conversationId, ResolvedModel model, ChatRequest request, TokenUsage? reported, string output, bool estimatedOnly)
    {
        var estimated = estimatedOnly || reported is null;
        var tokens = !estimatedOnly && reported is { } r
            ? r
            : new TokenUsage(ConversationService.EstimateRequestInput(request), ContextEstimator.EstimateText(output), 0, 0);
        try
        {
            await _usage.RecordAsync(
                    new UsageObservation(conversationId, UsagePurpose.ContextNote, ChatMode.Workbench, model, tokens, estimated, 0),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "記錄整理研究本的用量失敗。");
        }
    }
}
