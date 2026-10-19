using System.Text.Json;
using BetaMessages = global::Anthropic.Models.Beta.Messages;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Lumi.Providers.Anthropic;

/// <summary>
/// 把 Anthropic SDK 的串流事件（<see cref="BetaMessages.BetaRawMessageStreamEvent"/>）轉成 <see cref="ChatEvent"/>，
/// 並累積出完整的 <see cref="ChatMessage"/>。一個執行個體對應一次 <see cref="IChatProvider.StreamAsync"/> 呼叫。
/// </summary>
internal sealed class AnthropicStreamAccumulator
{
    private readonly string _modelId;
    private readonly List<BlockBuilder?> _blocks = [];

    private string? _messageId;
    private string? _responseModel;
    private TokenUsage _usage = TokenUsage.Zero;
    private string? _stopReasonRaw;

    public AnthropicStreamAccumulator(string modelId)
    {
        _modelId = modelId;
    }

    public List<ChatEvent> Process(BetaMessages.BetaRawMessageStreamEvent streamEvent)
    {
        var events = new List<ChatEvent>();

        if (streamEvent.TryPickStart(out var start))
        {
            _messageId = start.Message.ID;
            _responseModel = start.Message.Model;
            _usage = MergeUsage(_usage, start.Message.Usage);
            events.Add(new UsageEvent(_usage));
        }
        else if (streamEvent.TryPickContentBlockStart(out var blockStart))
        {
            EnsureCapacity(blockStart.Index);
            _blocks[(int)blockStart.Index] = BlockBuilder.FromStart(blockStart);
        }
        else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
        {
            var builder = _blocks[(int)blockDelta.Index]
                ?? throw new InvalidOperationException($"收到 index {blockDelta.Index} 的 delta，但沒有對應的 content_block_start。");

            if (blockDelta.Delta.TryPickText(out var textDelta))
            {
                builder.AppendText(textDelta.Text);
                events.Add(new TextDelta(textDelta.Text));
            }
            else if (blockDelta.Delta.TryPickThinking(out var thinkingDelta))
            {
                builder.AppendThinking(thinkingDelta.Thinking);
                events.Add(new ThinkingDelta(thinkingDelta.Thinking));
            }
            else if (blockDelta.Delta.TryPickSignature(out var signatureDelta))
            {
                builder.AppendSignature(signatureDelta.Signature);
            }
            else if (blockDelta.Delta.TryPickInputJson(out var inputDelta))
            {
                // 工具引數的 JSON 是分段的字串：累積到 content_block_stop，不在中途解析（半截 JSON 不合法）
                builder.AppendInputJson(inputDelta.PartialJson);
            }
            // 其他 delta 類型（citations 等）略過
        }
        else if (streamEvent.TryPickContentBlockStop(out var blockStop))
        {
            // 區塊內容已經累積完成；真正轉成 ContentBlock 在 BuildFinalEvent() 才做
            if (blockStop.Index >= 0 && blockStop.Index < _blocks.Count)
                _blocks[(int)blockStop.Index]?.MarkClosed();
        }
        else if (streamEvent.TryPickDelta(out var messageDelta))
        {
            // Delta.StopReason 是 ApiEnum<string, BetaStopReason>，有隱含轉換成 string（不能用 .ToString()，
            // 那印出來的是型別名稱，不是真正的 wire 值）。它可能是 null，所以先判斷再轉型。
            var stopReasonValue = messageDelta.Delta.StopReason;
            _stopReasonRaw = stopReasonValue is null ? null : (string)stopReasonValue;
            _usage = MergeUsage(_usage, messageDelta.Usage);
            events.Add(new UsageEvent(_usage));
        }
        else if (streamEvent.TryPickStop(out _))
        {
            events.Add(BuildFinalEvent());
        }
        // ping、未知事件類型：略過（API 以後可能新增事件，不能因此失敗）

        return events;
    }

    private ChatEvent BuildFinalEvent()
    {
        var stopReason = _stopReasonRaw;

        if (stopReason == "refusal")
        {
            // 已串出的部分內容是拒絕前的殘留，丟掉不存檔
            return new Failed(new ChatFailure(ChatFailureKind.Refused, "模型拒絕回答這個請求。", Retryable: false, RequestId: _messageId));
        }

        // 沒有完整收尾的工具呼叫（例如 max_tokens 把引數截斷了）不存：半截的引數執行不了，送回去也會被拒絕
        var content = _blocks.Where(b => b is not null && b.IsComplete).Select(b => b!.Build()).ToList();
        var message = new ChatMessage(
            Id: ChatMessage.NewId(),
            Role: ChatRole.Assistant,
            Content: content,
            CreatedAt: DateTimeOffset.UtcNow,
            Status: MessageStatus.Complete,
            ModelId: _responseModel ?? _modelId,
            Usage: _usage);

        return new Completed(message, MapStopReason(stopReason));
    }

    private static StopReason MapStopReason(string? raw) => raw switch
    {
        "end_turn" => StopReason.EndTurn,
        "max_tokens" => StopReason.MaxTokens,
        "stop_sequence" => StopReason.StopSequence,
        "tool_use" => StopReason.ToolUse,
        _ => StopReason.Other,
    };

    private static TokenUsage MergeUsage(TokenUsage current, BetaMessages.BetaUsage? incoming)
    {
        if (incoming is null) return current;
        return new TokenUsage(
            (int?)incoming.InputTokens ?? current.InputTokens,
            (int?)incoming.OutputTokens ?? current.OutputTokens,
            (int?)incoming.CacheCreationInputTokens ?? current.CacheCreationInputTokens,
            (int?)incoming.CacheReadInputTokens ?? current.CacheReadInputTokens);
    }

    private static TokenUsage MergeUsage(TokenUsage current, BetaMessages.BetaMessageDeltaUsage? incoming)
    {
        if (incoming is null) return current;
        return current with { OutputTokens = (int?)incoming.OutputTokens ?? current.OutputTokens };
    }

    private void EnsureCapacity(long index)
    {
        while (_blocks.Count <= index)
            _blocks.Add(null);
    }

    /// <summary>累積單一內容區塊的文字／思考內容／signature，完成後轉成 <see cref="ContentBlock"/>。</summary>
    private sealed class BlockBuilder
    {
        private enum Kind { Text, Thinking, RedactedThinking, ToolUse, Provider }

        private readonly Kind _kind;
        private readonly string _providerType = "";
        private readonly string _rawJson = "";
        private readonly System.Text.StringBuilder _text = new();
        private readonly System.Text.StringBuilder _signature = new();
        private string _redactedData = "";
        private string _toolId = "";
        private string _toolName = "";
        private bool _closed;

        private BlockBuilder(Kind kind, string providerType = "", string rawJson = "")
        {
            _kind = kind;
            _providerType = providerType;
            _rawJson = rawJson;
        }

        // ContentBlock 屬性的回傳型別（聯集）故意不寫出來：用 var 讓編譯器自己推斷，
        // 避免寫錯型別名稱；參數本身用具名型別，所以不是 dynamic 繫結。
        public static BlockBuilder FromStart(BetaMessages.BetaRawContentBlockStartEvent blockStartEvent)
        {
            var startBlock = blockStartEvent.ContentBlock;

            if (startBlock.TryPickBetaText(out var text))
            {
                var builder = new BlockBuilder(Kind.Text);
                builder._text.Append(text.Text);
                return builder;
            }

            if (startBlock.TryPickBetaThinking(out var thinking))
            {
                var builder = new BlockBuilder(Kind.Thinking);
                builder._text.Append(thinking.Thinking);
                builder._signature.Append(thinking.Signature);
                return builder;
            }

            if (startBlock.TryPickBetaRedactedThinking(out var redacted))
            {
                return new BlockBuilder(Kind.RedactedThinking) { _redactedData = redacted.Data };
            }

            if (startBlock.TryPickBetaToolUse(out var toolUse))
            {
                return new BlockBuilder(Kind.ToolUse) { _toolId = toolUse.ID, _toolName = toolUse.Name };
            }

            // 不認得的區塊（例如 fallback）：整段原樣存起來，不嘗試累積 delta
            string type = startBlock.GetType().Name;
            return new BlockBuilder(Kind.Provider, type, JsonSerializer.Serialize(startBlock));
        }

        public void AppendText(string text) => _text.Append(text);
        public void AppendThinking(string text) => _text.Append(text);
        public void AppendSignature(string signature) => _signature.Append(signature);
        public void AppendInputJson(string json) => _text.Append(json);
        public void MarkClosed() => _closed = true;

        /// <summary>其他區塊串流到哪都能用；工具呼叫要收尾了、而且引數是合法的 JSON 物件（沒有引數時是空字串，視為 <c>{}</c>）才算完整。</summary>
        public bool IsComplete => _kind != Kind.ToolUse || (_closed && (_text.Length == 0 || ToolJson.IsJsonObject(_text.ToString())));

        public ContentBlock Build() => _kind switch
        {
            Kind.Text => new TextBlock(_text.ToString()),
            Kind.Thinking => new ThinkingBlock(_text.ToString(), _signature.ToString()),
            Kind.RedactedThinking => new RedactedThinkingBlock(_redactedData),
            Kind.ToolUse => new ToolUseBlock(_toolId, _toolName, _text.Length == 0 ? "{}" : _text.ToString()),
            Kind.Provider => AnthropicProviderBlocks.FromRaw(_providerType, _rawJson),
            _ => throw new InvalidOperationException("未知的區塊種類"),
        };
    }
}
