using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.Core.Usage;

public enum UsagePurpose { Chat, Compaction }

/// <summary>一次 API 呼叫的用量與費用（<c>request_usage</c> 一列）。</summary>
/// <param name="Estimated">沒有供應商回報的 usage，以估算值計入。</param>
/// <param name="Price">null＝當時未設定單價。</param>
/// <param name="CostUsd">null＝未設定單價（不計入預算）。</param>
/// <param name="ToolDefinitionTokens">這次請求帶的工具定義大小（估算）。</param>
public sealed record UsageRecord(
    string Id,
    DateTimeOffset CreatedAt,
    string? ConversationId,
    UsagePurpose Purpose,
    ChatMode Mode,
    string? ModelSettingsId,
    string ModelId,
    string ConnectionName,
    TokenUsage Tokens,
    bool Estimated,
    PriceSnapshot? Price,
    decimal? CostUsd,
    int ToolDefinitionTokens)
{
    public static string NewId() => Guid.CreateVersion7().ToString();
}

/// <param name="Model">發出請求時的模型設定（單價從這裡拍快照）。</param>
public sealed record UsageObservation(
    string? ConversationId, UsagePurpose Purpose, ChatMode Mode, ResolvedModel Model,
    TokenUsage Tokens, bool Estimated, int ToolDefinitionTokens);

/// <summary>
/// <see cref="Chat.ConversationService"/> 每次呼叫供應商結束時回報一次。實作負責算費用、寫入、吞掉自己的錯誤（記錄失敗不能影響對話）。
/// </summary>
public interface IUsageRecorder
{
    Task RecordAsync(UsageObservation observation, CancellationToken ct = default);
}

public sealed class NullUsageRecorder : IUsageRecorder
{
    public Task RecordAsync(UsageObservation observation, CancellationToken ct = default) => Task.CompletedTask;
}
