using Avalonia.Controls;

namespace Lumi.App.Sprites;

/// <summary>
/// 小精靈的收音列（<see cref="SpriteVoiceBarViewModel"/> 的畫面）。純視覺：三種狀態各一列，按鈕都是 VM 的命令。
/// 狀態改變時由 <see cref="SpriteWindow"/> 呼叫 <see cref="FocusDefault"/>，把焦點放到該狀態的預設按鈕（焦點不會因為輸入框消失而掉到視窗外）。
/// </summary>
public partial class SpriteVoiceBar : UserControl
{
    public SpriteVoiceBar()
    {
        InitializeComponent();
    }

    /// <summary>錄音中＝「停止並編輯」（不會直接送出）；轉寫中＝「取消」；失敗＝「重試轉寫」。</summary>
    public void FocusDefault()
    {
        if (DataContext is not SpriteVoiceBarViewModel vm)
            return;
        if (vm.IsRecording)
            StopEditButton.Focus();
        else if (vm.IsTranscribing)
            CancelTranscribingButton.Focus();
        else if (vm.IsFailed)
            RetryVoiceButton.Focus();
    }
}
