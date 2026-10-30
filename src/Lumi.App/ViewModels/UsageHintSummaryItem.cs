using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Core.Usage;

namespace Lumi.App.ViewModels;

/// <summary>「這個月出現過的情況」的一條有紀錄的規則（plan/23-usage-cost.md 第 7.3 節）：點開看對話清單，被靜音的多一行「重新開啟」。</summary>
public sealed partial class UsageHintSummaryItem : ObservableObject
{
    public UsageHintSummaryItem(string rule, int conversationCount, bool isMuted, IReadOnlyList<UsageHintConversationItem> conversations)
    {
        Rule = rule;
        var (name, summary) = Labels(rule);
        Name = name;
        Summary = summary;
        CountText = string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_HintCountFormat, conversationCount.ToString(CultureInfo.InvariantCulture));
        Conversations = conversations;
        _isMuted = isMuted;
    }

    /// <summary>規則 id（<see cref="UsageHintIds"/>）。</summary>
    public string Rule { get; }

    /// <summary>規則名稱（<c>Hint_*Name</c>）。</summary>
    public string Name { get; }

    /// <summary>一句說明（<c>Hint_*Summary</c>）。</summary>
    public string Summary { get; }

    /// <summary>「N 則對話」（不同 conversation_id 的數量）。</summary>
    public string CountText { get; }

    /// <summary>對話清單（標題與日期）；查不到標題的對話不列。</summary>
    public IReadOnlyList<UsageHintConversationItem> Conversations { get; }

    public bool HasConversations => Conversations.Count > 0;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>規則的名稱與說明字串。</summary>
    public static (string Name, string Summary) Labels(string rule) => rule switch
    {
        UsageHintIds.ModelSwitch => (Strings.Hint_ModelSwitchName, Strings.Hint_ModelSwitchSummary),
        UsageHintIds.LongConversation => (Strings.Hint_LongConversationName, Strings.Hint_LongConversationSummary),
        UsageHintIds.StaleResume => (Strings.Hint_StaleResumeName, Strings.Hint_StaleResumeSummary),
        UsageHintIds.BigFile => (Strings.Hint_BigFileName, Strings.Hint_BigFileSummary),
        UsageHintIds.Images => (Strings.Hint_ImagesName, Strings.Hint_ImagesSummary),
        UsageHintIds.McpTools => (Strings.Hint_McpToolsName, Strings.Hint_McpToolsSummary),
        UsageHintIds.Regenerate => (Strings.Hint_RegenerateName, Strings.Hint_RegenerateSummary),
        UsageHintIds.LongOutput => (Strings.Hint_LongOutputName, Strings.Hint_LongOutputSummary),
        UsageHintIds.RepeatPaste => (Strings.Hint_RepeatPasteName, Strings.Hint_RepeatPasteSummary),
        _ => (rule, ""),
    };
}

/// <summary>提示的對話清單中的一列：標題與日期（本地日期 <c>M/d</c>）。</summary>
public sealed record UsageHintConversationItem(string ConversationId, string Title, string DateText);
