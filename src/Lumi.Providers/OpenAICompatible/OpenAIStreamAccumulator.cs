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
    private readonly List<ToolCallBuilder> _toolCalls = [];
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

        var content = new List<ContentBlock>();
        if (_text.Length > 0)
            content.Add(new TextBlock(_text.ToString()));

        // finish_reason: length 會把工具引數截斷：不完整的呼叫丟掉（執行不了，送回去也只會被伺服器拒絕）
        var truncated = _finishReason == "length";
        var toolUses = _toolCalls
            .Where(c => c.HasName && (!truncated || ToolJson.IsJsonObject(c.Arguments)))
            .OrderBy(c => c.Index)
            .Select(c => c.Build())
            .ToList();
        content.AddRange(toolUses);
        var message = new ChatMessage(
            ChatMessage.NewId(), ChatRole.Assistant, content, DateTimeOffset.UtcNow,
            MessageStatus.Complete, _responseModel ?? _requestedModel, _usage);
        // 有些伺服器（Ollama 等）有工具呼叫時 finish_reason 仍回 "stop"：以內容為準
        var reason = toolUses.Count > 0 && !truncated ? StopReason.ToolUse : MapFinishReason(_finishReason);
        events.Add(new Completed(message, reason));
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

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in toolCalls.EnumerateArray())
                    ProcessToolCall(call);
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

    /// <summary>
    /// 依 <c>index</c> 累積；寬容：index 缺漏時，有新的 id 就是新呼叫、沒有 id 就接在最後一個呼叫上；
    /// 整個呼叫放在單一 chunk、arguments 是空字串或分很多段都可以。
    /// </summary>
    private void ProcessToolCall(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object)
            return;

        var id = call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
        int? index = call.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number && indexElement.TryGetInt32(out var i)
            ? i
            : null;

        ToolCallBuilder? builder = null;
        if (index is { } wanted)
            builder = _toolCalls.FirstOrDefault(c => c.Index == wanted);
        else if (!string.IsNullOrEmpty(id))
            builder = _toolCalls.FirstOrDefault(c => c.Id == id);
        else if (_toolCalls.Count > 0)
            builder = _toolCalls[^1];

        if (builder is null)
        {
            builder = new ToolCallBuilder(index ?? _toolCalls.Count);
            _toolCalls.Add(builder);
        }

        if (!string.IsNullOrEmpty(id))
            builder.Id ??= id;

        if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } nameText)
                builder.Name ??= nameText;
            if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
                builder.AppendArguments(arguments.GetString() ?? "");
        }
    }

    private sealed class ToolCallBuilder(int index)
    {
        private readonly StringBuilder _arguments = new();

        public int Index { get; } = index;
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool HasName => !string.IsNullOrEmpty(Name);
        public string Arguments => _arguments.ToString();

        public void AppendArguments(string text) => _arguments.Append(text);

        /// <summary>沒有引數（空字串）等於 <c>{}</c>；伺服器沒給 id 時自己產生一個（回填結果時要用同一個 id 對應）。</summary>
        public ToolUseBlock Build() => new(
            string.IsNullOrEmpty(Id) ? "call_" + Guid.NewGuid().ToString("N")[..24] : Id,
            Name!,
            _arguments.Length == 0 ? "{}" : _arguments.ToString());
    }

    private static TokenUsage ParseUsage(JsonElement usage)
    {
        int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

        bool Has(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _);

        var prompt = Int(usage, "prompt_tokens");
        // 快取命中數：OpenAI 寫在 prompt_tokens_details.cached_tokens；DeepSeek 寫在頂層 prompt_cache_hit_tokens。兩者都沒有才是 0
        var cached = 0;
        if (usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            && Has(details, "cached_tokens"))
            cached = Int(details, "cached_tokens");
        else if (Has(usage, "prompt_cache_hit_tokens"))
            cached = Int(usage, "prompt_cache_hit_tokens");
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
