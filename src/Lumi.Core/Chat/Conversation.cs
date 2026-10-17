namespace Lumi.Core.Chat;

public sealed record Conversation(string Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}
