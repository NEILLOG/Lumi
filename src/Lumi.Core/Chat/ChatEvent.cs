using Lumi.Core.Tools;

namespace Lumi.Core.Chat;

/// <summary>
/// 串流事件。一次 <see cref="IChatProvider.StreamAsync"/> 呼叫剛好以一個 <see cref="Completed"/>
/// 或一個 <see cref="Failed"/> 結束，之後不再有事件；取消時丟 <see cref="OperationCanceledException"/>，
/// 不發 <see cref="Failed"/>（見 <see cref="IChatProvider"/> 的註解）。
/// </summary>
/// <remarks>
/// 一輪回應可以包含多次供應商呼叫（工具迴圈）：每次呼叫結束都有一個 <see cref="Completed"/>（該次的助理訊息），
/// 工具呼叫的事件（<see cref="ToolCallStarted"/>、<see cref="ApprovalRequired"/>、<see cref="ToolCallCompleted"/>）
/// 夾在兩個 <see cref="Completed"/> 之間。整輪以 <see cref="Failed"/>，或事件自然結束為終點。
/// </remarks>
public abstract record ChatEvent;

public sealed record TextDelta(string Text) : ChatEvent;

public sealed record ThinkingDelta(string Text) : ChatEvent;

/// <summary>Token 用量有更新就發一次；同一次呼叫可能發好幾次，以最後一次為準。</summary>
public sealed record UsageEvent(TokenUsage Tokens) : ChatEvent;

public sealed record Completed(ChatMessage Message, StopReason Reason) : ChatEvent;

public sealed record Failed(ChatFailure Failure) : ChatEvent;

/// <summary>開始執行一個工具呼叫（確認通過、真正執行之前也會先發，畫面據此顯示卡片）。</summary>
public sealed record ToolCallStarted(
    string CallId, string ToolName, string DisplayName, ToolPermission Permission, string? Target, string ArgumentsJson) : ChatEvent;

/// <summary>需要使用者確認：在等待之前發出，畫面據此在卡片內顯示確認區。</summary>
public sealed record ApprovalRequired(ToolApprovalRequest Request) : ChatEvent;

public sealed record ToolCallCompleted(string CallId, ToolResultBlock Result) : ChatEvent;

public enum StopReason { EndTurn, MaxTokens, StopSequence, ToolUse, Other }
