namespace Lumi.Core.Chat;

/// <summary><see cref="Tool"/> 是工具結果訊息：Anthropic 轉成 user 訊息裡的 tool_result，OpenAI 轉成 role: tool。畫面不把它畫成使用者泡泡。</summary>
public enum ChatRole { User, Assistant, Tool }

public enum MessageStatus { Complete, Stopped, Failed }

public sealed record ChatMessage(
    string Id,
    ChatRole Role,
    IReadOnlyList<ContentBlock> Content,
    DateTimeOffset CreatedAt,
    MessageStatus Status = MessageStatus.Complete,
    string? ModelId = null,
    TokenUsage? Usage = null)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}
