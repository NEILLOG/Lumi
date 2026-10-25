using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Core.Chat;

/// <summary>
/// 一次呼叫 <see cref="IChatProvider"/> 要送出的內容。<paramref name="Model"/> 帶著模型設定與它的連線
/// （Base URL、格式、金鑰名稱），Provider 從這裡拿；<paramref name="Messages"/> 是完整的對話歷史；
/// <paramref name="Tools"/> 是這次要提供給模型的工具定義（M15：對話第一次送出時凍結的快照；null 或空＝不帶工具定義）。
/// <paramref name="System"/> 是 system prompt（第 1、2 層，和模式無關）；<paramref name="TurnSuffix"/> 是模式指示（第 3、4 層），
/// Provider 把它放在請求的最尾端、快取斷點之後，不寫進 <paramref name="Messages"/>（M5 的 Q2）。
/// <paramref name="MaxOutputTokens"/> 覆寫模型設定的輸出上限（小精靈模式），null＝用模型設定。
/// </summary>
public sealed record ChatRequest(
    ResolvedModel Model,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ToolDefinition>? Tools = null,
    string? System = null,
    string? TurnSuffix = null,
    int? MaxOutputTokens = null)
{
    /// <summary>這次請求實際的輸出上限。</summary>
    public int EffectiveMaxOutputTokens => MaxOutputTokens ?? Model.Model.MaxOutputTokens;
}
