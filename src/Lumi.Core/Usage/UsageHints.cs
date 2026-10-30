namespace Lumi.Core.Usage;

public static class UsageHintIds
{
    public const string ModelSwitch = "model-switch";
    public const string LongConversation = "long-conversation";
    public const string StaleResume = "stale-resume";
    public const string BigFile = "big-file";
    public const string Images = "images";
    public const string McpTools = "mcp-tools";
    public const string Regenerate = "regenerate";
    public const string LongOutput = "long-output";
    public const string RepeatPaste = "repeat-paste";

    /// <summary>依優先順序（plan/23-usage-cost.md 第 7.2 節）。</summary>
    public static readonly IReadOnlyList<string> All =
        [ModelSwitch, LongConversation, StaleResume, BigFile, Images, McpTools, Regenerate, LongOutput, RepeatPaste];
}

/// <summary>提示的門檻。都是初始值（不是業界標準），之後用 usage_hints 與 request_usage 的實際資料校準（決定 10d）。</summary>
public static class UsageHintThresholds
{
    public const int ModelSwitchContextTokens = 20_000;
    public const double LongConversationContextRatio = 0.60;
    public const int LongConversationTurns = 40;
    public const int LongConversationRecentRequests = 10;
    public const int LongConversationAvgInputTokens = 60_000;
    public static readonly TimeSpan StaleResumeIdle = TimeSpan.FromMinutes(60);
    public const int StaleResumeContextTokens = 50_000;
    public const int BigFileTokens = 15_000;
    public const int BigFileRequestsAfter = 20;
    public const int ImagesCount = 3;
    public const int ImagesTokens = 5_000;
    public const int ImagesRequestsAfter = 10;
    public const int McpToolDefinitionTokens = 10_000;
    public static readonly TimeSpan McpToolsInterval = TimeSpan.FromDays(7);
    public const int RegenerateCount = 3;
    public const int LongOutputTokens = 4_000;
    public const int LongOutputCount = 3;
    public const int RepeatPasteTokens = 2_000;
}

public enum UsageHintOutcome { Shown, Dismissed, Muted, Acted, Suppressed }

public sealed record UsageHintEntry(string Id, string Rule, string? ConversationId, DateTimeOffset CreatedAt, UsageHintOutcome Outcome);

public interface IUsageHintLog
{
    Task AddAsync(UsageHintEntry entry, CancellationToken ct = default);

    Task SetOutcomeAsync(string id, UsageHintOutcome outcome, CancellationToken ct = default);

    /// <summary>這則對話、這條規則有沒有 Shown 以後的紀錄（Suppressed 不算）。</summary>
    Task<bool> WasShownAsync(string rule, string conversationId, CancellationToken ct = default);

    /// <summary>這條規則最近一次 Shown 以後紀錄的時間（Suppressed 不算）；沒有回 null（第 6 條的 7 天限制用）。</summary>
    Task<DateTimeOffset?> LastShownAsync(string rule, CancellationToken ct = default);

    /// <summary>[from, to) 之間的紀錄（用量頁「這個月出現過的情況」）。</summary>
    Task<IReadOnlyList<UsageHintEntry>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
