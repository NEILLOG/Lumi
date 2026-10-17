namespace Lumi.Core.Chat;

/// <summary>
/// 串流事件。一次 <see cref="IChatProvider.StreamAsync"/> 呼叫剛好以一個 <see cref="Completed"/>
/// 或一個 <see cref="Failed"/> 結束，之後不再有事件；取消時丟 <see cref="OperationCanceledException"/>，
/// 不發 <see cref="Failed"/>（見 <see cref="IChatProvider"/> 的註解）。
/// </summary>
/// <remarks>
/// M1 只會發出這五種。<c>ToolCallStarted</c>、<c>ToolCallCompleted</c>、<c>ApprovalRequired</c>
/// 要等 M3 有工具規格時再加（見 plan/01-skeleton-chat.md Q10），避免先猜欄位。
/// </remarks>
public abstract record ChatEvent;

public sealed record TextDelta(string Text) : ChatEvent;

public sealed record ThinkingDelta(string Text) : ChatEvent;

/// <summary>Token 用量有更新就發一次；同一次呼叫可能發好幾次，以最後一次為準。</summary>
public sealed record UsageEvent(TokenUsage Tokens) : ChatEvent;

public sealed record Completed(ChatMessage Message, StopReason Reason) : ChatEvent;

public sealed record Failed(ChatFailure Failure) : ChatEvent;

public enum StopReason { EndTurn, MaxTokens, StopSequence, Other }
