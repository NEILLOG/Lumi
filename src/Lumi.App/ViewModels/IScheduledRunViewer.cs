namespace Lumi.App.ViewModels;

/// <summary>
/// 打開一筆排程執行紀錄（M24）：歷史抽屜的「排程」分頁、通知的「查看」、引用卡的「查看原紀錄」都呼叫它。
/// 實作是 <see cref="WorkbenchViewModel"/>（工作台內容區換成排程紀錄檢視）；工作台沒開時由實作負責先開啟。
/// </summary>
public interface IScheduledRunViewer
{
    Task OpenScheduledRunAsync(string runId);
}
