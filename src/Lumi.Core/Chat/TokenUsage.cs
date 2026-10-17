namespace Lumi.Core.Chat;

public sealed record TokenUsage(int InputTokens, int OutputTokens, int CacheCreationInputTokens, int CacheReadInputTokens)
{
    public static readonly TokenUsage Zero = new(0, 0, 0, 0);
}
