using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>
/// 訊息列表裡的「以上內容已整理成摘要」分隔線（M22 22I）：右邊「查看摘要」，展開後顯示摘要全文（可選取）。
/// 只是畫面上的標記，不是訊息，不送給模型，也不存進資料庫。
/// </summary>
public partial class CompactionDividerViewModel : MessageViewModel
{
    public CompactionDividerViewModel(string summary) : base(isUser: false)
    {
        Summary = summary;
    }

    public override bool IsAssistant => false;

    /// <summary>摘要全文。</summary>
    public string Summary { get; }

    /// <summary>展開看摘要全文。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isSummaryOpen;

    /// <summary>分隔線右邊的文字：收起時「查看摘要」，展開時「收起摘要」。</summary>
    public string ToggleText => IsSummaryOpen ? Strings.Compact_HideSummary : Strings.Compact_ShowSummary;
}
