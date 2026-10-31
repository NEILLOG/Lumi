namespace Lumi.App.ViewModels;

/// <summary>排程紀錄檢視（M24 包 24F）。</summary>
public partial class WorkbenchViewModel : IScheduledRunViewer
{
    // TODO(M24 24F)：實作（工作台內容區換成排程紀錄檢視）
    public Task OpenScheduledRunAsync(string runId) => Task.CompletedTask;
}
