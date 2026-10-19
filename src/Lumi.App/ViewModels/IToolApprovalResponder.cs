using Lumi.Core.Tools;

namespace Lumi.App.ViewModels;

/// <summary>卡片上的確認按鈕把使用者的選擇交給等待中的對話迴圈。ViewModel 不碰實作，測試用假的。</summary>
public interface IToolApprovalResponder
{
    void Resolve(string callId, ApprovalDecision decision);
}

internal sealed class NullToolApprovalResponder : IToolApprovalResponder
{
    public static readonly NullToolApprovalResponder Instance = new();

    public void Resolve(string callId, ApprovalDecision decision)
    {
    }
}
