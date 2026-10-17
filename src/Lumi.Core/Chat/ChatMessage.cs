namespace Lumi.Core.Chat;

public enum ChatRole { User, Assistant }

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
