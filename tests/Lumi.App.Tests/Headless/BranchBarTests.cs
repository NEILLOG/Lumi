using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Branches;

namespace Lumi.App.Tests.Headless;

/// <summary>M36 36F：工作台的支線路徑列（各狀態的按鈕與字、Narrow 收起中間那一節、primary 的數量）。</summary>
public class BranchBarTests
{
    /// <summary>路徑列的按鈕要做的事：只記下呼叫的名稱。</summary>
    private sealed class RecordingHost : IBranchBarHost
    {
        public List<string> Calls { get; } = [];

        public Task MergeAsync(bool all) { Calls.Add(all ? "MergeAll" : "Merge"); return Task.CompletedTask; }

        public Task AbandonAsync() { Calls.Add("Abandon"); return Task.CompletedTask; }

        public Task BackAsync() { Calls.Add("Back"); return Task.CompletedTask; }

        public Task ReturnToBranchAsync() { Calls.Add("ReturnToBranch"); return Task.CompletedTask; }

        public Task OpenConversationByIdAsync(string conversationId) { Calls.Add($"Open:{conversationId}"); return Task.CompletedTask; }
    }

    private static BranchPathNode Node(string id, string title, int depth = 0, BranchStatus? status = null) => new(id, title, depth, status);

    private static Branch Child(string id, string parentId, int depth, BranchStatus status = BranchStatus.Active) =>
        new(id, parentId, "main", depth, "fork", "摘錄", DateTimeOffset.UnixEpoch, null, status, null, null, DateTimeOffset.UnixEpoch,
            status == BranchStatus.Active ? null : DateTimeOffset.UnixEpoch);

    private static BranchState FirstLevel(BranchStatus status = BranchStatus.Active) =>
        new([Node("main", "主線"), Node("b1", "量化是什麼", 1, status)], Child("b1", "main", 1, status), [], null);

    private static BranchState SecondLevel() =>
        new([Node("main", "主線"), Node("b1", "量化是什麼", 1, BranchStatus.Active), Node("b2", "GGUF", 2, BranchStatus.Active)],
            Child("b2", "b1", 2), [], null);

    /// <summary>把路徑列放進一個視窗（可以指定寬度分段的 class）。</summary>
    private static (BranchBarViewModel Model, BranchBar View, Window Window, RecordingHost Host) Show(BranchState? state, bool narrow = false,
        string? lockedTitle = null, bool canMerge = true, bool viewingRun = false)
    {
        var host = new RecordingHost();
        var model = new BranchBarViewModel(host);
        var view = new BranchBar { DataContext = model };
        var window = new Window { Content = view };
        if (narrow)
            window.Classes.Add("narrow");
        window.Show();
        model.Apply(state, lockedTitle, canMerge, viewingRun);
        window.UpdateLayout();
        return (model, view, window, host);
    }

    private static int VisiblePrimaries(BranchBar view) =>
        view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible);

    [AvaloniaFact]
    public void Active_first_level_shows_merge_main_and_abandon()
    {
        var (model, view, window, _) = Show(FirstLevel());

        Assert.True(model.Show);
        Assert.True(view.FindControl<Button>("MergeButton")!.IsEffectivelyVisible);
        Assert.Equal(Strings.Branch_Merge, model.MergeText);
        Assert.True(view.FindControl<Button>("AbandonButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("MergeUpButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Border>("DepthBadge")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Second_level_shows_split_merge_with_menu_and_badge()
    {
        var (model, view, window, _) = Show(SecondLevel());

        Assert.True(view.FindControl<StackPanel>("MergeSplit")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("MergeUpButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("MergeMenuButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("MergeButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Border>("DepthBadge")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("MiddleCrumbButton")!.IsEffectivelyVisible);
        Assert.Equal(Strings.Branch_MergeUp, model.MergeText);
        Assert.Equal(string.Format(Strings.Branch_MergeUpDetailFormat, "量化是什麼"), model.MergeUpDetailText);
        window.Close();
    }

    [AvaloniaFact]
    public void Finished_shows_status_and_back()
    {
        var (model, view, window, _) = Show(FirstLevel(BranchStatus.Merged));

        Assert.Equal(BranchBarMode.Finished, model.Mode);
        Assert.Equal(Strings.Branch_StatusMerged, model.StatusText);
        Assert.True(view.FindControl<Border>("StatusBadge")!.IsEffectivelyVisible);
        var back = view.FindControl<Button>("BackButton")!;
        Assert.True(back.IsEffectivelyVisible);
        Assert.Equal(Strings.Branch_BackToMain, back.Content);
        Assert.False(view.FindControl<Button>("AbandonButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("MergeButton")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Locked_shows_text_and_return()
    {
        var state = new BranchState([Node("main", "主線")], null, [Child("b1", "main", 1)], Child("b1", "main", 1));
        var (model, view, window, host) = Show(state, lockedTitle: "量化是什麼");

        Assert.Equal(BranchBarMode.Locked, model.Mode);
        Assert.Equal(string.Format(Strings.Branch_LockedFormat, "量化是什麼"), view.FindControl<TextBlock>("LockedText")!.Text);
        Assert.True(view.FindControl<Button>("ReturnButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("MainCrumbButton")!.IsEffectivelyVisible);
        Assert.False(view.FindControl<Button>("MergeButton")!.IsEffectivelyVisible);
        view.FindControl<Button>("ReturnButton")!.Command!.Execute(null);
        Assert.Equal(["ReturnToBranch"], host.Calls);
        window.Close();
    }

    [AvaloniaFact]
    public void Narrow_collapses_middle_crumb()
    {
        var (model, view, window, _) = Show(SecondLevel(), narrow: true);

        Assert.False(view.FindControl<Button>("MiddleCrumbButton")!.IsEffectivelyVisible);
        Assert.True(view.FindControl<Button>("MoreCrumbButton")!.IsEffectivelyVisible);
        Assert.Equal(Strings.Branch_MainCrumb, model.MainCrumbText);
        window.Close();
    }

    [AvaloniaFact]
    public void Hidden_when_viewing_a_scheduled_run_or_not_a_branch()
    {
        var (model, _, window, _) = Show(FirstLevel(), viewingRun: true);
        Assert.False(model.Show);
        window.Close();

        var (plain, _, plainWindow, _) = Show(new BranchState([Node("main", "主線")], null, [], null));
        Assert.False(plain.Show);
        plainWindow.Close();
    }

    [AvaloniaFact]
    public void Only_one_primary_per_bar()
    {
        // 第一層：唯一的 primary 是「帶回主線」
        var (_, first, firstWindow, _) = Show(FirstLevel());
        Assert.Equal(1, VisiblePrimaries(first));
        firstWindow.Close();

        // 第二層：「帶回上一層」與右邊的更多是同一組分開按鈕（計畫的示意圖兩顆都是 primary），其他按鈕不是
        var (_, second, secondWindow, _) = Show(SecondLevel());
        Assert.Equal(2, VisiblePrimaries(second));
        Assert.Equal(1, second.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("primary") && b.IsEffectivelyVisible && b.Name == "MergeUpButton"));
        secondWindow.Close();

        // 已結束：沒有 primary
        var (_, finished, finishedWindow, _) = Show(FirstLevel(BranchStatus.Abandoned));
        Assert.Equal(0, VisiblePrimaries(finished));
        finishedWindow.Close();
    }

    [AvaloniaFact]
    public void Empty_branch_disables_merge_and_explains()
    {
        var (model, view, window, _) = Show(FirstLevel(), canMerge: false);

        var merge = view.FindControl<Button>("MergeButton")!;
        Assert.False(merge.IsEnabled);
        Assert.Equal(Strings.Branch_MergeEmptyTip, model.MergeTip);
        window.Close();
    }
}
