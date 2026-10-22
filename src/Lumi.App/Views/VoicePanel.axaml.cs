using Avalonia.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 收音面板（ui-spec 6.3）。純視覺：三種狀態各一列，按鈕都是 <see cref="WorkbenchViewModel"/> 的命令。
/// 狀態改變時由 <see cref="ChatComposer"/> 呼叫 <see cref="FocusDefault"/>，把焦點放到這一狀態的預設按鈕（焦點不會因為輸入框消失而掉到視窗外）。
/// </summary>
public partial class VoicePanel : UserControl
{
    public VoicePanel()
    {
        InitializeComponent();
    }

    /// <summary>錄音中＝「停止並編輯」（不會直接送出）；轉寫中＝「取消」；失敗＝「重試轉寫」。</summary>
    public void FocusDefault()
    {
        if (DataContext is not WorkbenchViewModel vm)
            return;
        if (vm.IsVoiceRecording)
            VoiceStopEditButton.Focus();
        else if (vm.IsVoiceTranscribing)
            VoiceCancelTranscribeButton.Focus();
        else if (vm.IsVoiceFailed)
            VoiceRetryButton.Focus();
    }
}
