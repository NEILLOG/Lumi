using Lumi.Core.Outlook;
using Lumi.Core.Tools;

namespace Lumi.Tools.Outlook;

/// <summary>
/// Outlook 工具的共同部分（M30 設計 3）：<see cref="IConditionalTool"/> 的可用性、執行前先看狀態（不連線）、
/// 把 <see cref="OutlookException"/> 轉成錯誤結果。<see cref="OperationCanceledException"/> 不接，讓它往外丟。
/// </summary>
public abstract class OutlookToolBase<TArgs>(IOutlookClient client) : ToolBase<TArgs>, IConditionalTool where TArgs : class
{
    protected IOutlookClient Client { get; } = client;

    public ToolAvailability CheckAvailability() => OutlookToolSupport.Availability(Client);

    protected sealed override async Task<ToolResult> InvokeAsync(TArgs args, ToolContext context, CancellationToken ct)
    {
        // Outlook 沒有就地回覆，不呼叫 client 的其他方法
        var status = Client.GetStatus();
        if (status.State != OutlookState.Available)
            return ToolResult.Error(status.Message ?? OutlookToolText.NotRunning, "無法使用");

        try
        {
            return await RunAsync(args, context, ct);
        }
        catch (OutlookException ex)
        {
            // 訊息是白話繁中，直接回給模型
            return ToolResult.Error(ex.Message);
        }
    }

    /// <summary>Outlook 確認可以用之後才會呼叫。</summary>
    protected abstract Task<ToolResult> RunAsync(TArgs args, ToolContext context, CancellationToken ct);
}
