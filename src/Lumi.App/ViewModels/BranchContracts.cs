using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>帶回對話框的一步（C7：一路帶回主線是兩步，由深到淺）。</summary>
/// <param name="TargetIsMain">這一步帶回到主線（決定標題與主要按鈕的文字）。</param>
public sealed record BranchMergeStep(string ConversationId, string Title, string TargetTitle, bool TargetIsMain, int MessageCount);

/// <param name="ModelText">目前模型的「供應商 · model ID」；null＝沒有可用的模型。</param>
/// <param name="CostConversationTitle">費用算在哪一則（主線的標題）。</param>
public sealed record BranchMergeRequest(IReadOnlyList<BranchMergeStep> Steps, string? ModelText, string CostConversationTitle);

/// <param name="OpenTo">對話框關掉之後要開哪一則：全部帶回是最後一步的上一層；中途取消是最後一條已帶回支線的上一層；一步都沒帶回是 null（留在原地）。</param>
public sealed record BranchMergeResult(Conversation? OpenTo);

public interface IBranchMergePrompt
{
    Task<BranchMergeResult> PromptAsync(BranchMergeRequest request);
}

/// <summary>訊息列表最上方的出處卡片要的資料（目前是支線時才有）。</summary>
/// <param name="ParentTitle">上一層的標題；上一層是主線時是 null（顯示「從主線分出來」）。</param>
public sealed record BranchOriginInfo(string? ParentTitle, DateTimeOffset ForkMessageAt, string ForkExcerpt, string? Quote);

/// <summary>上一層的一張摘要卡。</summary>
/// <param name="DeliveredMessageId">交出它的使用者訊息；還沒交出是 null（卡片放在最後面）。</param>
public sealed record BranchCardInfo(
    string ConversationId, string Title, string Summary, DateTimeOffset CreatedAt, DateTimeOffset EndedAt,
    DateTimeOffset ForkMessageAt, int MessageCount, int ChildCount, string? DeliveredMessageId);

/// <summary>交給 <see cref="ConversationTimeline.Build"/> 的支線資訊（36F 讀好）。</summary>
public sealed record BranchTimelineInfo(BranchOriginInfo? Origin, IReadOnlyList<BranchCardInfo> Cards)
{
    public static BranchTimelineInfo None { get; } = new(null, []);
}

/// <summary>「問這段」：哪一則訊息、選了什麼（已經過 <c>BranchBlocks.NormalizeQuote</c>）。</summary>
public sealed record BranchQuoteRequest(MessageViewModel Message, string Quote);
