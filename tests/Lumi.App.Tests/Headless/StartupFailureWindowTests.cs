using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Views;
using Lumi.Infrastructure.Startup;

namespace Lumi.App.Tests.Headless;

/// <summary>M9 9C：啟動失敗說明視窗的結構與按鈕層級（不依賴 DI，只用 token）。</summary>
public class StartupFailureWindowTests
{
    private static StartupFailureReport Report() => new(
        StartupFailureKind.DatabaseLocked,
        StartupFailureReport.ReasonFor(StartupFailureKind.DatabaseLocked),
        "/Users/tester/Library/Application Support/Lumi/logs",
        "Lumi 啟動失敗\n原因分類：DatabaseLocked");

    [AvaloniaFact]
    public void Window_ShowsReasonAndLogFolder()
    {
        var report = Report();
        var window = new StartupFailureWindow(report);
        window.Show();
        window.UpdateLayout();

        Assert.Equal(report.Reason, window.FindControl<TextBlock>("ReasonText")!.Text);
        Assert.Equal(report.LogDirectory, window.FindControl<SelectableTextBlock>("LogPathText")!.Text);
        Assert.Equal(Strings.StartupFailure_Title, window.Title);
        Assert.False(window.FindControl<TextBlock>("CopiedText")!.IsVisible);
    }

    [AvaloniaFact]
    public void Buttons_FollowTheLevelRules_ExitIsThePrimaryAndCopyIsSecondary()
    {
        var window = new StartupFailureWindow(Report());
        window.Show();
        window.UpdateLayout();

        var exit = window.FindControl<Button>("ExitButton")!;
        var copy = window.FindControl<Button>("CopyButton")!;

        Assert.Equal(Strings.StartupFailure_Exit, exit.Content);
        Assert.Equal(Strings.StartupFailure_CopyDetails, copy.Content);
        Assert.Contains("primary", exit.Classes);
        Assert.Contains("secondary", copy.Classes);
        Assert.DoesNotContain("primary", copy.Classes);
        Assert.True(exit.IsDefault);
    }

    [AvaloniaFact]
    public void ExitButton_ClosesTheWindow_SoTheCallerCanEndTheProcess()
    {
        var window = new StartupFailureWindow(Report());
        var closed = 0;
        window.Closed += (_, _) => closed++;
        window.Show();
        window.UpdateLayout();

        window.FindControl<Button>("ExitButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void Window_Details_AreTheReportText()
    {
        var report = Report();
        var window = new StartupFailureWindow(report);

        Assert.Equal(report.Details, window.Details);
    }
}
