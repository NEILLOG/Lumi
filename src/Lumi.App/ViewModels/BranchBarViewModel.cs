using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Branches;

namespace Lumi.App.ViewModels;

/// <summary>路徑列的三種狀態：支線進行中、支線已結束（唯讀）、上一層被鎖住（4.7、4.8）。</summary>
public enum BranchBarMode { Active, Finished, Locked }

/// <summary>路徑列的按鈕要做的事，由 <see cref="WorkbenchViewModel"/> 實作。</summary>
public interface IBranchBarHost
{
    /// <summary>帶回；<paramref name="all"/> 是第二層的「一路帶回主線」。</summary>
    Task MergeAsync(bool all);

    Task AbandonAsync();

    Task BackAsync();

    Task ReturnToBranchAsync();

    Task OpenConversationByIdAsync(string conversationId);
}

/// <summary>
/// 工作台的支線路徑列（M36 36F，ui-spec 第 2 節與 4.8）：左邊是路徑（主線 › 支線），右邊是動作。
/// 狀態由 <see cref="Apply"/> 從 <see cref="BranchState"/> 算出，按鈕呼叫 <see cref="IBranchBarHost"/>。
/// </summary>
public sealed partial class BranchBarViewModel : ObservableObject
{
    private readonly IBranchBarHost _host;

    public BranchBarViewModel(IBranchBarHost host) => _host = host;

    /// <summary>要不要顯示：支線進行中、已結束，或上一層被鎖住；看排程紀錄時隱藏。</summary>
    [ObservableProperty]
    private bool _show;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsFinished), nameof(IsLocked), nameof(ShowCrumbs), nameof(ShowMiddleCrumbs),
        nameof(ShowMiddleCrumb), nameof(ShowMoreCrumb), nameof(ShowDepthBadge), nameof(ShowMerge), nameof(ShowMergeSplit), nameof(ShowAbandon),
        nameof(ShowBack), nameof(ShowLocked), nameof(ShowStatus))]
    private BranchBarMode _mode;

    /// <summary>支線的層數（0＝主線、1＝第一層、2＝第二層）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDepthBadge), nameof(ShowMiddleCrumbs), nameof(ShowMiddleCrumb), nameof(ShowMoreCrumb),
        nameof(ShowMerge), nameof(ShowMergeSplit), nameof(MergeText), nameof(BackText))]
    private int _depth;

    /// <summary>寬度分段：wide／compact／narrow（由 code-behind 從視窗的 class 讀，見 <c>BranchBar.axaml.cs</c>）。Compact 起主線節只寫「主線」，Narrow 把中間節收成「…」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNarrow), nameof(MainCrumbText), nameof(ShowMiddleCrumb), nameof(ShowMoreCrumb))]
    private string _widthClass = "wide";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MainCrumbText))]
    private string? _mainConversationId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MainCrumbText))]
    private string _mainTitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiddleCrumbText), nameof(MergeUpDetailText))]
    private string? _middleConversationId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MiddleCrumbText), nameof(MergeUpDetailText))]
    private string _middleTitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentCrumbText))]
    private string _currentTitle = "";

    /// <summary>已結束時的狀態字：「已帶回」或「已放棄」。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>上一層被鎖住時的說明：「支線「{標題}」還沒結束。…」。</summary>
    [ObservableProperty]
    private string _lockedText = "";

    /// <summary>有訊息才能帶回（空支線不能帶回）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MergeTip))]
    private bool _canMerge;

    public bool IsActive => Mode == BranchBarMode.Active;

    public bool IsFinished => Mode == BranchBarMode.Finished;

    public bool IsLocked => Mode == BranchBarMode.Locked;

    public bool IsNarrow => WidthClass == "narrow";

    /// <summary>主線那一節：wide 寫「主線 · {標題}」，其餘寫「主線」。</summary>
    public string MainCrumbText => WidthClass == "wide"
        ? string.Format(CultureInfo.CurrentCulture, Strings.Branch_MainCrumbFormat, MainTitle)
        : Strings.Branch_MainCrumb;

    public string MiddleCrumbText => string.Format(CultureInfo.CurrentCulture, Strings.Branch_CrumbFormat, MiddleTitle);

    public string CurrentCrumbText => string.Format(CultureInfo.CurrentCulture, Strings.Branch_CrumbFormat, CurrentTitle);

    public bool ShowCrumbs => Mode != BranchBarMode.Locked;

    /// <summary>第二層時的中間那一節（第一層支線）。</summary>
    public bool ShowMiddleCrumbs => ShowCrumbs && Depth >= BranchLimits.MaxDepth;

    public bool ShowMiddleCrumb => ShowMiddleCrumbs && !IsNarrow;

    public bool ShowMoreCrumb => ShowMiddleCrumbs && IsNarrow;

    public bool ShowDepthBadge => ShowCrumbs && Depth >= BranchLimits.MaxDepth;

    /// <summary>第一層：單一的「帶回主線」。</summary>
    public bool ShowMerge => Mode == BranchBarMode.Active && Depth < BranchLimits.MaxDepth;

    /// <summary>第二層：「帶回上一層」＋ 更多（一路帶回主線）的分開按鈕。</summary>
    public bool ShowMergeSplit => Mode == BranchBarMode.Active && Depth >= BranchLimits.MaxDepth;

    public string MergeText => Depth >= BranchLimits.MaxDepth ? Strings.Branch_MergeUp : Strings.Branch_Merge;

    public string MergeUpDetailText => string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeUpDetailFormat, MiddleTitle);

    public string MergeTip => CanMerge ? "" : Strings.Branch_MergeEmptyTip;

    public bool ShowAbandon => Mode == BranchBarMode.Active;

    public bool ShowStatus => Mode == BranchBarMode.Finished;

    public bool ShowBack => Mode == BranchBarMode.Finished;

    public string BackText => Depth >= BranchLimits.MaxDepth ? Strings.Branch_BackToParent : Strings.Branch_BackToMain;

    public bool ShowLocked => Mode == BranchBarMode.Locked;

    /// <summary>依目前的支線狀態更新路徑列。<paramref name="state"/> 是 null 或不是支線（且沒有被鎖住）時隱藏。</summary>
    public void Apply(BranchState? state, string? lockedChildTitle, bool canMerge, bool viewingRun)
    {
        CanMerge = canMerge;
        if (state is null || viewingRun || (!state.IsBranch && !state.IsLocked))
        {
            Show = false;
            return;
        }

        Show = true;
        Mode = state.IsLocked ? BranchBarMode.Locked : state.IsFinished ? BranchBarMode.Finished : BranchBarMode.Active;
        Depth = state.Depth;
        var path = state.Path;
        MainConversationId = path[0].ConversationId;
        MainTitle = path[0].Title;
        MiddleConversationId = state.Depth >= BranchLimits.MaxDepth ? path[1].ConversationId : null;
        MiddleTitle = state.Depth >= BranchLimits.MaxDepth ? path[1].Title : "";
        CurrentTitle = path[^1].Title;
        StatusText = state.Current?.Status switch
        {
            BranchStatus.Merged => Strings.Branch_StatusMerged,
            BranchStatus.Abandoned => Strings.Branch_StatusAbandoned,
            _ => Strings.Branch_StatusActive,
        };
        LockedText = lockedChildTitle is null ? "" : string.Format(CultureInfo.CurrentCulture, Strings.Branch_LockedFormat, lockedChildTitle);
    }

    [RelayCommand]
    private Task MergeAsync() => _host.MergeAsync(all: false);

    [RelayCommand]
    private Task MergeAllAsync() => _host.MergeAsync(all: true);

    [RelayCommand]
    private Task AbandonAsync() => _host.AbandonAsync();

    [RelayCommand]
    private Task BackAsync() => _host.BackAsync();

    [RelayCommand]
    private Task ReturnToBranchAsync() => _host.ReturnToBranchAsync();

    [RelayCommand]
    private Task OpenMainAsync() => MainConversationId is { } id ? _host.OpenConversationByIdAsync(id) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenMiddleAsync() => MiddleConversationId is { } id ? _host.OpenConversationByIdAsync(id) : Task.CompletedTask;
}
