using System.Text.Json.Serialization;

namespace Lumi.Core.Chat;

/// <summary>
/// 一則訊息裡的一個內容區塊。對話歷史只能往後加，已送出的區塊不能修改或重排
/// （新模型會檢查 thinking 區塊的 signature 與前面的對話是否一致，改過歷史會回 400）。
/// 見 agent.md 第 7 節。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(ThinkingBlock), "thinking")]
[JsonDerivedType(typeof(RedactedThinkingBlock), "redacted_thinking")]
[JsonDerivedType(typeof(ProviderBlock), "provider")]
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>
/// <paramref name="Signature"/> 是供應商的不透明資料，必須原樣送回，不能自己產生或修改。
/// </summary>
public sealed record ThinkingBlock(string Thinking, string Signature) : ContentBlock;

/// <summary>供應商判斷內容需要隱藏時回傳的不透明資料，一樣要原樣送回。</summary>
public sealed record RedactedThinkingBlock(string Data) : ContentBlock;

/// <summary>
/// 不認得或供應商專屬的區塊（例如 Anthropic 的 fallback 區塊）：存原始 JSON 原樣送回。
/// 只有產生它的供應商會認得；換成別家供應商時應該略過，不要送出。
/// </summary>
public sealed record ProviderBlock(string Provider, string Type, string RawJson) : ContentBlock;
