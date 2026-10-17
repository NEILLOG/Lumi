using System.Text.Json;
using BetaMessages = global::Anthropic.Models.Beta.Messages;
using Lumi.Core.Chat;

namespace Lumi.Providers.Anthropic;

/// <summary>
/// Anthropic 專屬、我們不特別建模的區塊（目前是 fallback 區塊）：存原始 JSON，
/// 送回時原樣還原，不嘗試理解內容。
/// </summary>
internal static class AnthropicProviderBlocks
{
    public static BetaMessages.BetaContentBlockParam ToParam(ProviderBlock block) =>
        JsonSerializer.Deserialize<BetaMessages.BetaContentBlockParam>(block.RawJson)
        ?? throw new InvalidOperationException($"ProviderBlock 的 RawJson 無法還原：{block.Type}");

    public static ProviderBlock FromRaw(string type, string rawJson) => new("anthropic", type, rawJson);
}
