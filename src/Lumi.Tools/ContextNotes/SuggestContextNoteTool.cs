using System.ComponentModel;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Tools;

namespace Lumi.Tools.ContextNotes;

public sealed record SuggestContextNoteArgs(
    [property: Description("建議的研究本目標，一兩句話，說出這次討論要決定或弄清楚什麼")] string Goal);

/// <summary>
/// 模型建議開研究本（M35 D9）：只記下建議紀錄，之後由畫面的詢問卡片讓使用者決定。
/// 工具不建立研究本、不開視窗；每則對話最多建議一次（建議紀錄是唯一的依據）。
/// </summary>
public sealed class SuggestContextNoteTool(IContextNoteStore notes, IConversationStore conversations) : ToolBase<SuggestContextNoteArgs>
{
    /// <summary>工具名稱。畫面靠它認出建議卡片。</summary>
    public const string ToolName = "suggest_context_note";

    private const string DescriptionText =
        "建議使用者把這則對話整理成「研究本」（一份跟著討論自動更新的文件，記下目標、決定、放棄的選項與理由）。"
        + "只在使用者正在調查、研究、比較方案或要做決定，而且已經來回討論至少三輪、看起來還會繼續時呼叫。"
        + "閒聊、單次問答、寫程式或修改文件的工作不要呼叫。每則對話最多呼叫一次。"
        + "呼叫後畫面會出現詢問卡片，由使用者決定；你照常回答使用者的問題，不要在回答裡再提這件事，也不要替使用者決定。";

    public override ToolDescriptor Descriptor { get; } = new(
        ToolName, "建議開研究本", DescriptionText,
        ToolPermission.ReadOnly, ToolModes.Workbench, TimeSpan.FromSeconds(5),
        Group: ToolGroups.Notes, Summary: "在適合的時候詢問你要不要把討論整理成研究本。");

    protected override ToolPreview Describe(SuggestContextNoteArgs args, ToolContext context) => new(args.Goal?.Trim(), null);

    protected override async Task<ToolResult> InvokeAsync(SuggestContextNoteArgs args, ToolContext context, CancellationToken ct)
    {
        var conversationId = context.ConversationId;
        var conversation = string.IsNullOrEmpty(conversationId)
            ? null
            : await conversations.GetConversationAsync(conversationId, ct);
        // 找不到對話（不該發生）：回一般錯誤，不要誤報成「已經建議過」
        if (conversation is null)
            return ToolResult.Error("找不到這則對話，不能建議研究本。", "找不到對話");

        if (conversation.Kind == ConversationKind.ScheduledRun)
            return ToolResult.Error("排程執行不能建立研究本。", "排程不能使用");

        var goal = args.Goal?.Trim() ?? "";
        if (goal.Length == 0 || goal.Length > ContextNoteLimits.GoalMaxLength)
            return ToolResult.Error("目標要寫成 1 到 200 個字的一兩句話。", "目標不合格式");

        if (await notes.GetByConversationAsync(conversationId, ct) is not null)
            return ToolResult.Error("這則對話已經有研究本，不要再建議。", "已經有研究本");

        if (await notes.GetSuggestionAsync(conversationId, ct) is not null
            || !await notes.TryMarkSuggestedAsync(conversationId, context.Time.GetUtcNow(), ct))
            return ToolResult.Error("這則對話已經建議過，不要再建議。", "已經建議過");

        return ToolResult.Ok("已經在畫面上詢問使用者。照常回答使用者的問題，不要再提這件事。", goal);
    }
}
