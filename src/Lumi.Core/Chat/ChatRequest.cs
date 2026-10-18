using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Core.Chat;

/// <summary>
/// 一次呼叫 <see cref="IChatProvider"/> 要送出的內容。<paramref name="Model"/> 帶著模型設定與它的連線
/// （Base URL、格式、金鑰名稱），Provider 從這裡拿；<paramref name="Messages"/> 是完整的對話歷史；
/// <paramref name="Tools"/> 是這次要提供給模型的工具（null 或空＝不帶工具定義）。
/// </summary>
public sealed record ChatRequest(ResolvedModel Model, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ITool>? Tools = null);
