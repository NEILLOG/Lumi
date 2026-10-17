namespace Lumi.Core.Chat;

/// <summary>
/// 一次呼叫 <see cref="IChatProvider"/> 要送出的內容。<paramref name="Messages"/> 是完整的對話歷史
/// （user／assistant 交替），由呼叫端（1D 的對話流程）組好；1C 不處理 system prompt（M5 的 Prompt 分層）。
/// </summary>
public sealed record ChatRequest(string ModelId, IReadOnlyList<ChatMessage> Messages, int MaxTokens);
