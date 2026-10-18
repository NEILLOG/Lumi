using BetaMessages = global::Anthropic.Models.Beta.Messages;
using Lumi.Core.Chat;

namespace Lumi.Providers.Anthropic;

/// <summary>把 Core 的 <see cref="ChatRequest"/> 轉成 Anthropic SDK 的 beta 請求參數。</summary>
internal static class AnthropicRequestConverter
{
    public static BetaMessages.MessageCreateParams ToCreateParams(ChatRequest request, AnthropicProviderOptions options)
    {
        var settings = request.Model.Model;
        var model = settings.ModelId;
        var maxTokens = settings.MaxOutputTokens;
        var messages = request.Messages.Select(ToMessageParam).Where(m => m is not null).Select(m => m!).ToList();
        BetaMessages.BetaOutputConfig? outputConfig = options.Effort is { Length: > 0 } effort
            ? new BetaMessages.BetaOutputConfig { Effort = effort }
            : null;
        var fallbacks = options.UseServerFallbacks;

        // ShowThinking 關掉時完全不送 thinking 參數，讓模型用自己的預設（不支援 adaptive 的舊模型也能用）。
        // 打開時要求 summarized：不設的話 claude-opus-5-5 仍會思考，但內容是空字串，UI 沒東西可顯示。
        // 各種組合分開寫成物件初始設定式：這些屬性是 init-only，而且 null 分支經過 implicit operator 會觸發
        // 可為 Null 的警告（TreatWarningsAsErrors）。
        // 候補模型用陣列形式指名 Opus 4.8（M1 實作紀錄：SDK 沒有 "default" 純量形式的寫法）。
        return (settings.ShowThinking, fallbacks) switch
        {
            (true, true) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
                Thinking = new BetaMessages.BetaThinkingConfigAdaptive { Display = BetaMessages.Display.Summarized },
                Fallbacks = new List<BetaMessages.BetaFallbackParam> { new(global::Anthropic.Models.Messages.Model.ClaudeOpus4_8) },
                Betas = ["server-side-fallback-2026-06-01"],
            },
            (true, false) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
                Thinking = new BetaMessages.BetaThinkingConfigAdaptive { Display = BetaMessages.Display.Summarized },
            },
            (false, true) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
                Fallbacks = new List<BetaMessages.BetaFallbackParam> { new(global::Anthropic.Models.Messages.Model.ClaudeOpus4_8) },
                Betas = ["server-side-fallback-2026-06-01"],
            },
            (false, false) => new BetaMessages.MessageCreateParams
            {
                Model = model, MaxTokens = maxTokens, Messages = messages, OutputConfig = outputConfig,
            },
        };
    }

    /// <summary>別家供應商的區塊略過；略過後沒有內容的訊息整則不送（回 null）。</summary>
    private static BetaMessages.BetaMessageParam? ToMessageParam(ChatMessage message)
    {
        var content = message.Content.Where(IsSendable).Select(ToContentBlockParam).ToList();
        if (content.Count == 0)
            return null;

        return new BetaMessages.BetaMessageParam
        {
            Role = message.Role == ChatRole.User ? BetaMessages.Role.User : BetaMessages.Role.Assistant,
            Content = content,
        };
    }

    private static bool IsSendable(ContentBlock block) =>
        block is not ProviderBlock provider || provider.Provider == AnthropicProviderBlocks.ProviderName;

    private static BetaMessages.BetaContentBlockParam ToContentBlockParam(ContentBlock block) => block switch
    {
        TextBlock t => new BetaMessages.BetaTextBlockParam { Text = t.Text },
        ThinkingBlock t => new BetaMessages.BetaThinkingBlockParam { Thinking = t.Thinking, Signature = t.Signature },
        RedactedThinkingBlock r => new BetaMessages.BetaRedactedThinkingBlockParam { Data = r.Data },
        ProviderBlock p => AnthropicProviderBlocks.ToParam(p),   // 只剩 Anthropic 自己的（見 IsSendable）
        _ => throw new NotSupportedException($"未知的內容區塊類型：{block.GetType()}"),
    };
}
