using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Lumi.Core.Models;

namespace Lumi.Core.Chat;

/// <summary>
/// 最小對話流程：存使用者訊息 → 組請求 → 呼叫 <see cref="IChatProvider"/> → 轉發事件 → 存助理訊息。
/// 事件與 <see cref="IChatProvider"/> 的約定相同；取消時丟 <see cref="OperationCanceledException"/>。
/// </summary>
public sealed class ConversationService
{
    public const int TitleMaxLength = 30;

    private readonly IChatProvider _provider;
    private readonly IConversationStore _store;
    private readonly IModelCatalog _catalog;
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public ConversationService(IChatProvider provider, IConversationStore store, IModelCatalog catalog)
    {
        _provider = provider;
        _store = store;
        _catalog = catalog;
    }

    /// <summary>先把使用者訊息存檔，再串流助理回應。第一則訊息會順便設定對話標題。</summary>
    public async IAsyncEnumerable<ChatEvent> SendAsync(
        string conversationId, string userText, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        Begin(conversationId);
        try
        {
            var existing = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
            var userMessage = new ChatMessage(
                ChatMessage.NewId(), ChatRole.User, [new TextBlock(userText)], DateTimeOffset.UtcNow);
            await _store.AppendMessageAsync(conversationId, userMessage, ct).ConfigureAwait(false);
            if (existing.Count == 0)
                await _store.RenameAsync(conversationId, MakeTitle(userText), ct).ConfigureAwait(false);

            await foreach (var chatEvent in StreamCoreAsync(conversationId, ct).ConfigureAwait(false))
                yield return chatEvent;
        }
        finally
        {
            End(conversationId);
        }
    }

    /// <summary>不新增使用者訊息，用目前的歷史再要一次回應（重試、存好金鑰後重送）。最後一則必須是使用者訊息。</summary>
    public async IAsyncEnumerable<ChatEvent> RetryAsync(
        string conversationId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Begin(conversationId);
        try
        {
            await foreach (var chatEvent in StreamCoreAsync(conversationId, ct).ConfigureAwait(false))
                yield return chatEvent;
        }
        finally
        {
            End(conversationId);
        }
    }

    private async IAsyncEnumerable<ChatEvent> StreamCoreAsync(
        string conversationId, [EnumeratorCancellation] CancellationToken ct)
    {
        var history = await _store.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        var requestMessages = BuildRequestMessages(history);
        if (requestMessages.Count == 0 || requestMessages[^1].Role != ChatRole.User)
            throw new InvalidOperationException("最後一則訊息必須是使用者訊息才能要求回應。");

        // 工作台用的模型＝設定裡的工作台預設模型（M2 的 Q3）；M7 的小精靈改用 SpriteModelId
        var model = _catalog.Resolve(_catalog.Current.WorkbenchModelId);
        if (model is null)
        {
            yield return new Failed(new ChatFailure(ChatFailureKind.ModelNotConfigured, "還沒有可用的模型，請先到設定加入連線與模型。", Retryable: false));
            yield break;
        }

        var request = new ChatRequest(model, requestMessages);
        var receivedText = new StringBuilder();

        await using var enumerator = _provider.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 已收到的文字存成「已停止」；未完成的思考內容沒有 signature，不保留。
                // 存檔不受取消影響。
                if (receivedText.Length > 0)
                {
                    var stopped = new ChatMessage(
                        ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(receivedText.ToString())],
                        DateTimeOffset.UtcNow, MessageStatus.Stopped, model.Model.ModelId);
                    await _store.AppendMessageAsync(conversationId, stopped, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }

            if (!hasNext)
                yield break;

            var chatEvent = enumerator.Current;
            switch (chatEvent)
            {
                case TextDelta delta:
                    receivedText.Append(delta.Text);
                    break;
                case Completed completed:
                    // 先存檔再通知，UI 收到 Completed 時資料庫已經是最新狀態
                    await _store.AppendMessageAsync(conversationId, completed.Message, CancellationToken.None).ConfigureAwait(false);
                    break;
                // Failed：不存（Q15）
            }

            yield return chatEvent;
        }
    }

    /// <summary>
    /// 把存檔的歷史轉成要送出的訊息：只送 Complete／Stopped、略過沒有內容的訊息、
    /// 連續同角色的訊息合併成一則（API 要求 user／assistant 交替）。只影響送出的請求，不改存檔。
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildRequestMessages(IReadOnlyList<ChatMessage> history)
    {
        var result = new List<ChatMessage>();
        foreach (var message in history)
        {
            if (message.Status is not (MessageStatus.Complete or MessageStatus.Stopped))
                continue;
            if (!HasContent(message))
                continue;

            if (result.Count > 0 && result[^1].Role == message.Role)
            {
                var previous = result[^1];
                result[^1] = previous with { Content = [.. previous.Content, .. message.Content] };
            }
            else
            {
                result.Add(message);
            }
        }

        // API 要求第一則是使用者訊息
        while (result.Count > 0 && result[0].Role != ChatRole.User)
            result.RemoveAt(0);

        return result;
    }

    public static string MakeTitle(string userText)
    {
        var singleLine = string.Join(' ', userText.Split((char[])['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(singleLine);
        // 以字形叢集為單位截斷，避免把 emoji 切壞
        return elements.Length <= TitleMaxLength
            ? singleLine
            : singleLine[..elements[TitleMaxLength]] + "…";
    }

    private static bool HasContent(ChatMessage message) => message.Content.Any(block => block switch
    {
        TextBlock t => t.Text.Length > 0,
        _ => true,
    });

    private void Begin(string conversationId)
    {
        if (!_active.TryAdd(conversationId, 0))
            throw new InvalidOperationException("這則對話已經有回應在進行中。");
    }

    private void End(string conversationId) => _active.TryRemove(conversationId, out _);
}
