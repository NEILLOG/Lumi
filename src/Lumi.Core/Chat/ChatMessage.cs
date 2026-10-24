namespace Lumi.Core.Chat;

/// <summary><see cref="Tool"/> 是工具結果訊息：Anthropic 轉成 user 訊息裡的 tool_result，OpenAI 轉成 role: tool。畫面不把它畫成使用者泡泡。</summary>
public enum ChatRole { User, Assistant, Tool }

public enum MessageStatus { Complete, Stopped, Failed }

/// <param name="TurnSuffix">
/// 送出這則訊息（它當時是請求的最後一則）時附在後面的模式指示（M5 的 Q2，2026-10-25 修改）。
/// 存起來、之後每次請求都原樣帶上，歷史才是「只往後加」——Anthropic 的 thinking 區塊簽章綁定它之前的所有內容，
/// 事後把這段拿掉等於改了歷史，帶著 thinking 區塊的請求會被回 400。只有使用者與工具結果訊息會有；畫面不顯示。
/// </param>
public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    IReadOnlyList<ContentBlock> Content,
    DateTimeOffset CreatedAt,
    MessageStatus Status = MessageStatus.Complete,
    string? ModelId = null,
    TokenUsage? Usage = null,
    string? TurnSuffix = null)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}
