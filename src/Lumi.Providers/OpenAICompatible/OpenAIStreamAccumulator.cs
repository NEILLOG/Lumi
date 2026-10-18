using System.Text;
using System.Text.Json;
using Lumi.Core.Chat;

namespace Lumi.Providers.OpenAICompatible;

/// <summary>
/// 把 Chat Completions 的串流 chunk（每個 SSE 事件的 data）轉成 <see cref="ChatEvent"/>，並累積出完整訊息。
/// 一個執行個體對應一次呼叫。寬容：不認得的欄位略過；伺服器沒回 usage 時，訊息的 Usage 是 null。
/// </summary>
internal sealed class OpenAIStreamAccumulator
{
    private readonly string _requestedModel;
    private readonly StringBuilder _text = new();
    private string? _responseModel;
    private string? _finishReason;
    private TokenUsage? _usage;

    public OpenAIStreamAccumulator(string requestedModel)
    {
        _requestedModel = requestedModel;
    }

    /// <summary>已經產生了 <see cref="Completed"/> 或 <see cref="Failed"/>。</summary>
    public bool IsFinished { get; private set; }

    public List<ChatEvent> Process(string data)
    {
        var events = new List<ChatEvent>();
        if (IsFinished)
            return events;

        var trimmed = data.Trim();
        if (trimmed == "[DONE]")
        {
            events.AddRange(Finish(sawDone: true));
            return events;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException)
        {
            return events;   // 不是 JSON 的事件（例如某些伺服器的註解文字）略過
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return events;

            if (root.TryGetProperty("error", out var error) && error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            {
                IsFinished = true;
                events.Add(new Failed(new ChatFailure(ChatFailureKind.StreamError, "伺服器在串流中回報錯誤。", Retryable: true)));
                return events;
            }

            if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
                _responseModel = model.GetString();

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                    ProcessChoice(choice, events);
            }

            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                _usage = ParseUsage(usage);
                events.Add(new UsageEvent(_usage));
            }
        }

        return events;
    }

    /// <summary>串流結束（連線正常關閉）時呼叫。沒收到 <c>[DONE]</c> 也沒有 finish_reason 視為意外中斷。</summary>
    public List<ChatEvent> Finish(bool sawDone = false)
    {
        var events = new List<ChatEvent>();
        if (IsFinished)
            return events;
        IsFinished = true;

        if (_finishReason is null && !sawDone)
        {
            events.Add(new Failed(new ChatFailure(ChatFailureKind.StreamError, "回應意外中斷。", Retryable: true)));
            return events;
        }

        if (_finishReason == "content_filter")
        {
            // 內容被過濾：已串出的部分不存檔（同 Anthropic 的拒絕）
            events.Add(new Failed(new ChatFailure(ChatFailureKind.Refused, "伺服器因內容過濾拒絕了這個回應。", Retryable: false)));
            return events;
        }

        ContentBlock[] content = _text.Length > 0 ? [new TextBlock(_text.ToString())] : [];
        var message = new ChatMessage(
            ChatMessage.NewId(), ChatRole.Assistant, content, DateTimeOffset.UtcNow,
            MessageStatus.Complete, _responseModel ?? _requestedModel, _usage);
        events.Add(new Completed(message, MapFinishReason(_finishReason)));
        return events;
    }

    private void ProcessChoice(JsonElement choice, List<ChatEvent> events)
    {
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 } text)
            {
                _text.Append(text);
                events.Add(new TextDelta(text));
            }

            // Ollama 等伺服器的推理內容：只顯示，不存成 ThinkingBlock（沒有 signature，不能送回）
            if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String
                && reasoning.GetString() is { Length: > 0 } thinking)
            {
                events.Add(new ThinkingDelta(thinking));
            }
        }

        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            _finishReason = finish.GetString();
    }

    private static TokenUsage ParseUsage(JsonElement usage)
    {
        int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

        var prompt = Int(usage, "prompt_tokens");
        var cached = usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Int(details, "cached_tokens")
            : 0;
        // OpenAI 的 prompt_tokens 含快取；我們的 InputTokens 不含（和 Anthropic 一致），快取另外記
        return new TokenUsage(Math.Max(prompt - cached, 0), Int(usage, "completion_tokens"), 0, cached);
    }

    private static StopReason MapFinishReason(string? reason) => reason switch
    {
        "stop" or null => StopReason.EndTurn,
        "length" => StopReason.MaxTokens,
        _ => StopReason.Other,
    };
}
