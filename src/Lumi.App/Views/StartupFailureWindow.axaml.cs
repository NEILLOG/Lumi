using Avalonia.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;

namespace Lumi.App.Views;

/// <summary>
/// 啟動失敗時的說明視窗（M9 9C）。建構子只吃已經組好的 <see cref="StartupFailureReport"/>，不從 DI 取任何東西。
/// 「結束」與視窗的關閉鈕都是關閉視窗；結束處理程序（非 0 的結束碼）由呼叫端訂閱 <see cref="Window.Closed"/> 負責。
/// </summary>
public partial class StartupFailureWindow : Window
{
    private readonly StartupFailureReport _report;

    public StartupFailureWindow(StartupFailureReport report)
    {
        _report = report;
        InitializeComponent();
        ReasonText.Text = report.Reason;
        LogPathText.Text = report.LogDirectory;
        CopyButton.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_report.Details);
                CopiedText.IsVisible = true;
            }
        };
        ExitButton.Click += (_, _) => Close();
    }

    /// <summary>詳細資訊的純文字（測試與複製用）。</summary>
    public string Details => _report.Details;
}
