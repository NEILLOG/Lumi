using System.Globalization;
using System.Text;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Chat;

/// <summary>第一則訊息送出後，在背景產生的標題已經存進資料庫。</summary>
public sealed record ConversationTitleGenerated(string ConversationId, string Title);

/// <summary>
/// 自動產生對話標題：第一則訊息送出時，先用原文當標題（<see cref="ConversationService.MakeTitle"/>），
/// 再在背景請同一個模型取一個容易辨識的短標題，成功才換掉。
/// </summary>
public interface IConversationTitler
{
    /// <summary>標題換好了（從背景執行緒發出，畫面要自己切回 UI 執行緒）。</summary>
    event EventHandler<ConversationTitleGenerated>? TitleGenerated;

    /// <summary>
    /// 產生並存檔。<paramref name="fallbackTitle"/> 是已經存好的原文標題：存檔前標題已經不是它（使用者改過名、對話被刪了）就不動。
    /// 原文太短、沒有模型、呼叫失敗、回答不能用時，什麼都不做（標題維持原文）。不丟例外。
    /// </summary>
    Task GenerateAsync(string conversationId, string userText, string fallbackTitle, ChatMode mode, CancellationToken ct = default);
}

/// <summary>不產生標題（測試與預設）。</summary>
public sealed class NullConversationTitler : IConversationTitler
{
    public event EventHandler<ConversationTitleGenerated>? TitleGenerated { add { } remove { } }

    public Task GenerateAsync(string conversationId, string userText, string fallbackTitle, ChatMode mode, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>
/// 用目前工作台的模型產生標題：不帶工具、不要求 thinking、輸出上限 <see cref="MaxOutputTokens"/>；用量記成 <see cref="UsagePurpose.Title"/>。
/// 只送第一則訊息的文字（最多 <see cref="MaxInputChars"/> 字），不送檔案與圖片。
/// </summary>
public sealed class ConversationTitler(
    IChatProvider provider,
    IConversationStore store,
    IModelCatalog catalog,
    IUsageRecorder? usage = null,
    ILogger<ConversationTitler>? logger = null) : IConversationTitler
{
    /// <summary>原文不超過這麼多字（字形叢集）就直接當標題，不另外產生。</summary>
    public const int ShortTextLength = 12;

    /// <summary>送給模型的訊息最多幾字。</summary>
    public const int MaxInputChars = 2000;

    /// <summary>輸出上限：標題很短，但要留一些空間給會先「想一下」的模型。</summary>
    public const int MaxOutputTokens = 200;

    private static readonly char[] Wrappers = ['「', '」', '『', '』', '"', '\'', '“', '”', '‘', '’', '`', '*', '#', ' ', '　'];
    private static readonly char[] EndPunctuation = ['。', '.', '！', '!', '；', ';', '，', ','];

    private readonly IUsageRecorder _usage = usage ?? new NullUsageRecorder();
    private readonly ILogger<ConversationTitler> _logger = logger ?? NullLogger<ConversationTitler>.Instance;

    public event EventHandler<ConversationTitleGenerated>? TitleGenerated;

    /// <summary>原文夠長才值得另外產生（太短的原文本身就是好標題）。</summary>
    public static bool ShouldGenerate(string userText) =>
        new StringInfo(ConversationService.MakeTitle(userText).TrimEnd('…')).LengthInTextElements > ShortTextLength;

    public async Task GenerateAsync(string conversationId, string userText, string fallbackTitle, ChatMode mode, CancellationToken ct = default)
    {
        if (!ShouldGenerate(userText) || catalog.Resolve(catalog.Current.WorkbenchModelId) is not { } resolved)
            return;

        try
        {
            var model = new ResolvedModel(resolved.Model with { ShowThinking = false }, resolved.Connection);
            var request = new ChatRequest(model, [BuildMessage(userText)], null, null, null, Math.Min(MaxOutputTokens, resolved.Model.MaxOutputTokens));

            var text = new StringBuilder();
            Completed? completed = null;
            TokenUsage? lastUsage = null;
            await foreach (var chatEvent in provider.StreamAsync(request, ct).ConfigureAwait(false))
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
                        _logger.LogInformation("產生對話標題失敗（{Kind}），維持原文標題。", f.Failure.Kind);
                        await RecordAsync(conversationId, mode, model, request, lastUsage, text.ToString(), estimatedOnly: true).ConfigureAwait(false);
                        return;
                }
            }

            await RecordAsync(conversationId, mode, model, request, completed?.Message.Usage ?? lastUsage, text.ToString(), estimatedOnly: false).ConfigureAwait(false);

            var answer = completed is null
                ? text.ToString()
                : string.Concat(completed.Message.Content.OfType<TextBlock>().Select(t => t.Text));
            if (Clean(answer) is not { } title || title == fallbackTitle)
                return;

            // 使用者在這段時間改過名、清除了小精靈對話、或刪掉了對話：不動
            var conversation = await store.GetConversationAsync(conversationId, ct).ConfigureAwait(false);
            if (conversation is null || conversation.Title != fallbackTitle)
                return;

            await store.RenameAsync(conversationId, title, ct).ConfigureAwait(false);
            TitleGenerated?.Invoke(this, new ConversationTitleGenerated(conversationId, title));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // 標題只是方便辨識，失敗絕不能影響對話
            _logger.LogWarning(ex, "產生對話標題時發生錯誤，維持原文標題。");
        }
    }

    /// <summary>
    /// 把模型的回答整理成標題：取第一個非空白行，去掉「標題：」、引號、Markdown 符號與結尾的句號，再照原文標題的規則截斷。
    /// 整理完不到 2 個字回 null（不能用，維持原文）。
    /// </summary>
    public static string? Clean(string? answer)
    {
        var line = (answer ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        foreach (var prefix in (string[])["標題：", "標題:", "Title:", "title:"])
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                line = line[prefix.Length..].Trim();
        }
        // 引號、Markdown 符號與結尾的標點可能交錯（「**標題**。」），反覆去掉直到不再變化
        string previous;
        do
        {
            previous = line;
            line = line.Trim(Wrappers).TrimEnd(EndPunctuation);
        }
        while (line != previous);
        if (new StringInfo(line).LengthInTextElements < 2)
            return null;
        return ConversationService.MakeTitle(line);
    }

    private static ChatMessage BuildMessage(string userText)
    {
        var excerpt = userText.Length > MaxInputChars ? userText[..MaxInputChars] : userText;
        // 訊息內容裡的結尾標籤跳脫，避免提前結束（同 <web_content> 的做法）
        excerpt = excerpt.Replace("</message", "<\\/message", StringComparison.OrdinalIgnoreCase);
        var text = PromptText.Get("title") + "\n<message>\n" + excerpt + "\n</message>";
        return new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);
    }

    /// <summary>記用量（失敗不影響標題）。沒有供應商回報的 usage 時用估算值。</summary>
    private async Task RecordAsync(
        string conversationId, ChatMode mode, ResolvedModel model, ChatRequest request, TokenUsage? reported, string output, bool estimatedOnly)
    {
        var estimated = estimatedOnly || reported is null;
        var tokens = !estimatedOnly && reported is { } r
            ? r
            : new TokenUsage(ConversationService.EstimateRequestInput(request), ContextEstimator.EstimateText(output), 0, 0);
        try
        {
            await _usage.RecordAsync(new UsageObservation(conversationId, UsagePurpose.Title, mode, model, tokens, estimated, 0), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "記錄產生標題的用量失敗。");
        }
    }
}
