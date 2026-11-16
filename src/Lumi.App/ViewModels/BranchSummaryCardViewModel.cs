using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>
/// 上一層的支線摘要卡（M36，4.8）：標題「支線 · {標題}」、已帶回、時間與訊息數、摘要全文（可選取）、
/// 「看完整支線」。交出摘要的那則使用者訊息上方放已交出的卡；還沒交出的放在最後，附一行說明。
/// 只是畫面上的標記，不送給模型（送給模型的是 <c>BranchSummary</c> 區塊）。
/// </summary>
public partial class BranchSummaryCardViewModel : MessageViewModel
{
    /// <summary>剛帶回（結束時間在這之內）才播進入動畫。</summary>
    private static readonly TimeSpan EnterWindow = TimeSpan.FromSeconds(5);

    public BranchSummaryCardViewModel(BranchCardInfo info, TimeProvider time) : base(isUser: false)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(time);
        Info = info;

        var age = time.GetUtcNow() - info.EndedAt;
        PlayEnterAnimation = IsPending && age >= TimeSpan.Zero && age <= EnterWindow;
    }

    public BranchCardInfo Info { get; }

    /// <summary>支線的對話 id（「看完整支線」用）。</summary>
    public string ConversationId => Info.ConversationId;

    /// <summary>標題：「支線 · {標題}」。</summary>
    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.Branch_CardTitleFormat, Info.Title);

    /// <summary>Caption：「{M/d HH:mm}～{HH:mm} · {N} 則訊息 · 從 Lumi {HH:mm} 的回答分出來」（有下一層時多「含 K 條支線」）。</summary>
    public string MetaText
    {
        get
        {
            var start = Info.CreatedAt.ToLocalTime();
            var end = Info.EndedAt.ToLocalTime();
            var endText = end.Date == start.Date
                ? end.ToString("HH:mm", CultureInfo.InvariantCulture)
                : end.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
            var startText = start.ToString("M/d HH:mm", CultureInfo.InvariantCulture);
            var fork = Info.ForkMessageAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

            return Info.ChildCount > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.Branch_CardMetaWithChildrenFormat, startText, endText, Info.MessageCount, Info.ChildCount, fork)
                : string.Format(CultureInfo.CurrentCulture, Strings.Branch_CardMetaFormat, startText, endText, Info.MessageCount, fork);
        }
    }

    /// <summary>摘要全文（<see cref="MarkdownView"/> 畫）。</summary>
    public string Summary => Info.Summary;

    /// <summary>還沒交出（下一則訊息會一起讀到）。</summary>
    public bool IsPending => Info.DeliveredMessageId is null;

    /// <summary>剛帶回：View 播一次進入動畫（Opacity 0→1，200ms）。</summary>
    public bool PlayEnterAnimation { get; }

    /// <summary>摘要展開與否。預設展開，不記住。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleTip))]
    private bool _isExpanded = true;

    /// <summary>標頭右邊收合按鈕的 Tooltip。</summary>
    public string ToggleTip => IsExpanded ? Strings.Branch_CardCollapse : Strings.Branch_CardExpand;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public override bool IsAssistant => false;

    public override bool IsMarker => true;
}
