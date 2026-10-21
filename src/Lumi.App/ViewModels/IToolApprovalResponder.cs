using Lumi.Core.Tools;

namespace Lumi.App.ViewModels;

/// <summary>誰回答了哪一個確認。</summary>
public sealed record ApprovalResolved(string CallId, ApprovalDecision Decision);

/// <summary>卡片上的確認按鈕把使用者的選擇交給等待中的對話迴圈。ViewModel 不碰實作，測試用假的。</summary>
public interface IToolApprovalResponder
{
    void Resolve(string callId, ApprovalDecision decision);

    /// <summary>
    /// 不論是哪一邊回答（工作台的卡片、小精靈的氣泡）都發出，讓另一邊同步更新（ui-spec 2.2）。
    /// 預設實作是永遠不會發出的空事件，不需要同步的假實作可以不管它。
    /// </summary>
    event EventHandler<ApprovalResolved>? Resolved
    {
        add { }
        remove { }
    }
}

internal sealed class NullToolApprovalResponder : IToolApprovalResponder
{
    public static readonly NullToolApprovalResponder Instance = new();

    public void Resolve(string callId, ApprovalDecision decision)
    {
    }
}
