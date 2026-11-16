namespace Lumi.App.ViewModels;

/// <summary>上一層的支線摘要卡（M36）。TODO(36G)：Phase 0 只有空殼。</summary>
public partial class BranchSummaryCardViewModel : MessageViewModel
{
    private readonly TimeProvider _time;

    public BranchSummaryCardViewModel(BranchCardInfo info, TimeProvider time) : base(isUser: false)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(time);
        Info = info;
        _time = time;
    }

    public BranchCardInfo Info { get; }

    // TODO(36G)：實作後拿掉
    internal TimeProvider Time => _time;

    public override bool IsAssistant => false;

    public override bool IsMarker => true;
}
