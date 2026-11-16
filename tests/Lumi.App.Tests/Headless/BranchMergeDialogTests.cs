using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using AvaloniaTextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M36 36H：帶回對話框的視窗（標題與步驟、Esc 取消、只有一個主要按鈕、最後一步確認後關閉）。</summary>
public class BranchMergeDialogTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static BranchMergeRequest TwoSteps() =>
        new([new BranchMergeStep("b2", "第二層", "第一層", false, 2), new BranchMergeStep("b1", "第一層", "主線", true, 5)],
            "測試 · test-model", "主線");

    private static BranchMergeRequest OneStep() =>
        new([new BranchMergeStep("b1", "支線", "主線", true, 3)], "測試 · test-model", "主線");

    private static (BranchMergeDialog Dialog, BranchMergeDialogViewModel Vm) Open(FakeBranchService service, BranchMergeRequest request)
    {
        var vm = new BranchMergeDialogViewModel(service, request);
        var dialog = new BranchMergeDialog(vm);
        dialog.Show();
        dialog.UpdateLayout();
        return (dialog, vm);
    }

    private static void KeyDown(Interactive target, Key key) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    [AvaloniaFact]
    public void Renders_two_step_title()
    {
        var (dialog, vm) = Open(new FakeBranchService(), TwoSteps());

        Assert.Equal(Strings.Branch_MergeDialogTitleUp, dialog.Title);
        Assert.Equal(vm.StepText, dialog.FindControl<AvaloniaTextBlock>("StepBlock")!.Text);
        Assert.Equal(string.Format(Strings.Branch_MergeStepFormat, 1, 2, "第二層", "第一層"),
            dialog.FindControl<AvaloniaTextBlock>("StepBlock")!.Text);
        Assert.Equal(Strings.Branch_MergeNext, dialog.FindControl<Button>("ConfirmButton")!.Content);
        Assert.Equal("摘要:b2", vm.SummaryText);
        Assert.Equal("摘要:b2", dialog.FindControl<TextBox>("SummaryBox")!.Text);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Single_step_hides_the_step_line_and_names_the_main_conversation()
    {
        var (dialog, _) = Open(new FakeBranchService(), OneStep());

        Assert.Equal(Strings.Branch_MergeDialogTitle, dialog.Title);
        Assert.False(dialog.FindControl<AvaloniaTextBlock>("StepBlock")!.IsVisible);
        Assert.Equal(Strings.Branch_Merge, dialog.FindControl<Button>("ConfirmButton")!.Content);
        Assert.Equal(string.Format(Strings.Branch_MergeCostFormat, "測試 · test-model", "主線"),
            dialog.FindControl<AvaloniaTextBlock>("CostBlock")!.Text);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Escape_cancels()
    {
        var service = new FakeBranchService();
        var (dialog, vm) = Open(service, OneStep());

        KeyDown(dialog.FindControl<TextBox>("SummaryBox")!, Key.Escape);

        Assert.True(vm.Completion.IsCompleted);
        Assert.Null(vm.Completion.Result.OpenTo);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void Only_one_primary()
    {
        var (dialog, _) = Open(new FakeBranchService(), TwoSteps());

        var primaries = dialog.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("primary"));

        Assert.Equal(1, primaries);
        dialog.Close();
    }

    [AvaloniaFact]
    public void Last_step_confirm_closes_the_dialog_with_the_parent()
    {
        var service = new FakeBranchService();
        var main = new Conversation("main", "主線", Now, Now);
        service.Parents["b1"] = main;
        var (dialog, vm) = Open(service, OneStep());

        var confirm = dialog.FindControl<Button>("ConfirmButton")!;
        Assert.True(confirm.IsEnabled);
        confirm.Command!.Execute(null);

        Assert.Same(main, vm.Completion.Result.OpenTo);
        Assert.False(dialog.IsVisible);
    }

    [AvaloniaFact]
    public void Empty_summary_disables_confirm()
    {
        var service = new FakeBranchService();
        service.Drafts["b1"] = new Queue<BranchSummaryDraft>([new BranchSummaryDraft(null, new ChatFailure(ChatFailureKind.ModelNotConfigured, "失敗", Retryable: false))]);
        var (dialog, vm) = Open(service, OneStep());

        Assert.False(vm.CanConfirm);
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.False(dialog.FindControl<Button>("ConfirmButton")!.IsEnabled);
        Assert.Equal(string.Format(Strings.Branch_MergeFailedFormat, "失敗"), dialog.FindControl<AvaloniaTextBlock>("ErrorBlock")!.Text);
        vm.Cancel();
        dialog.Close();
    }
}
