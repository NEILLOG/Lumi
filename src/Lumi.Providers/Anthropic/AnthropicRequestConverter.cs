using BetaMessages = global::Anthropic.Models.Beta.Messages;
using Lumi.Core.Chat;

namespace Lumi.Providers.Anthropic;

/// <summary>把 Core 的 <see cref="ChatRequest"/> 轉成 Anthropic SDK 的 beta 請求參數。</summary>
internal static class AnthropicRequestConverter
{
    public static BetaMessages.MessageCreateParams ToCreateParams(ChatRequest request, AnthropicDevOptions options)
    {
        var model = request.ModelId;
        var maxTokens = request.MaxTokens;
        var messages = request.Messages.Select(ToMessageParam).ToList();
        // 一律要求 summarized：不設的話 claude-opus-5-5 仍會思考，但 thinking 內容是空字串，
        // UI 就沒有東西可以顯示「思考過程」。
        var thinking = new BetaMessages.BetaThinkingConfigAdaptive { Display = BetaMessages.Display.Summarized };
        BetaMessages.BetaOutputConfig? outputConfig = options.Effort is { Length: > 0 } effort
            ? new BetaMessages.BetaOutputConfig { Effort = effort }
            : null;

        if (!options.UseServerFallbacks)
        {
            return new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, Thinking = thinking, OutputConfig = outputConfig,
            };
        }

        // 用陣列形式指名候補模型（而不是 "default" 純量形式）：這是 README 給出的、已確認可用的寫法；
        // 拒絕多半是誤擋，Opus 4.8 是官方建議的候補（cyber 類別的拒絕會導到它）。另外分支寫，是為了
        // 避免 Fallbacks（null 分支）觸發 implicit operator 的可為 Null 警告（TreatWarningsAsErrors）。
        return new BetaMessages.MessageCreateParams
        {
            Model = model, MaxTokens = maxTokens, Messages = messages, Thinking = thinking, OutputConfig = outputConfig,
            Fallbacks = new List<BetaMessages.BetaFallbackParam> { new(global::Anthropic.Models.Messages.Model.ClaudeOpus4_8) },
            Betas = ["server-side-fallback-2026-06-01"],
        };
    }

    private static BetaMessages.BetaMessageParam ToMessageParam(ChatMessage message) => new()
    {
        Role = message.Role == ChatRole.User ? BetaMessages.Role.User : BetaMessages.Role.Assistant,
        Content = message.Content.Select(ToContentBlockParam).ToList(),
    };

    private static BetaMessages.BetaContentBlockParam ToContentBlockParam(ContentBlock block) => block switch
    {
        TextBlock t => new BetaMessages.BetaTextBlockParam { Text = t.Text },
        ThinkingBlock t => new BetaMessages.BetaThinkingBlockParam { Thinking = t.Thinking, Signature = t.Signature },
        RedactedThinkingBlock r => new BetaMessages.BetaRedactedThinkingBlockParam { Data = r.Data },
        // 其他供應商的區塊（含別家的 ProviderBlock）不送出；只有產生它的供應商認得
        ProviderBlock p when p.Provider == "anthropic" => AnthropicProviderBlocks.ToParam(p),
        ProviderBlock => throw new InvalidOperationException("不應該送出其他供應商的 ProviderBlock；呼叫端應先篩選。"),
        _ => throw new NotSupportedException($"未知的內容區塊類型：{block.GetType()}"),
    };
}
