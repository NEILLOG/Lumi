namespace Lumi.App.ViewModels;

/// <summary>支線訊息列表最上方的出處卡片（M36）。TODO(36G)：Phase 0 只有空殼。</summary>
public partial class BranchOriginViewModel : MessageViewModel
{
    public BranchOriginViewModel(BranchOriginInfo info) : base(isUser: false)
    {
        ArgumentNullException.ThrowIfNull(info);
        Info = info;
    }

    public BranchOriginInfo Info { get; }

    public override bool IsAssistant => false;

    public override bool IsMarker => true;
}
