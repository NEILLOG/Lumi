using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Branches;
using Lumi.Core.Chat;

namespace Lumi.App.Tests;

/// <summary>M36 36H：帶回對話框的 ViewModel（一步或兩步、取消的結果、失敗與無模型時自己寫、字數上限、重新整理）。</summary>
public class BranchMergeDialogViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static BranchMergeStep Step(string id, string title, string target, bool targetIsMain, int count = 3) =>
        new(id, title, target, targetIsMain, count);

    private static BranchMergeRequest OneStep(string model = "測試 · test-model") =>
        new([Step("b1", "支線", "主線", true)], model, "主線");

    private static BranchMergeRequest TwoSteps(string model = "測試 · test-model") =>
        new([Step("b2", "第二層", "第一層", false, 2), Step("b1", "第一層", "主線", true, 5)], model, "主線");

    private static Conversation Conv(string id) => new(id, $"上一層 {id}", Now, Now, ConversationKind.Chat);

    [Fact]
    public async Task One_step_merge_returns_parent()
    {
        var service = new FakeBranchService();
        service.Parents["b1"] = Conv("main");
        var vm = new BranchMergeDialogViewModel(service, OneStep());

        await vm.StartAsync();
        Assert.Equal("摘要:b1", vm.SummaryText);
        await vm.ConfirmAsync();

        var result = await vm.Completion;
        Assert.Same(service.Parents["b1"], result.OpenTo);
        Assert.Equal(["Draft:b1", "Merge:b1:摘要:b1"], service.Calls);
    }

    [Fact]
    public async Task Two_steps_merge_deepest_first_and_draft_second_after_first()
    {
        var service = new FakeBranchService();
        service.Parents["b2"] = Conv("b1");
        service.Parents["b1"] = Conv("main");
        var vm = new BranchMergeDialogViewModel(service, TwoSteps());

        await vm.StartAsync();
        await vm.ConfirmAsync();
        await vm.ConfirmAsync();

        var result = await vm.Completion;
        Assert.Same(service.Parents["b1"], result.OpenTo);
        Assert.Equal(["Draft:b2", "Merge:b2:摘要:b2", "Draft:b1", "Merge:b1:摘要:b1"], service.Calls);
    }

    [Fact]
    public async Task Cancel_after_first_step_returns_first_parent()
    {
        var service = new FakeBranchService();
        service.Parents["b2"] = Conv("b1");
        var vm = new BranchMergeDialogViewModel(service, TwoSteps());

        await vm.StartAsync();
        await vm.ConfirmAsync();
        vm.Cancel();

        var result = await vm.Completion;
        Assert.Same(service.Parents["b2"], result.OpenTo);
    }

    [Fact]
    public async Task Cancel_before_any_returns_null()
    {
        var service = new FakeBranchService();
        var vm = new BranchMergeDialogViewModel(service, TwoSteps());

        await vm.StartAsync();
        vm.Cancel();

        var result = await vm.Completion;
        Assert.Null(result.OpenTo);
        Assert.Equal(["Draft:b2"], service.Calls);
    }

    [Fact]
    public async Task No_model_allows_manual_text()
    {
        var service = new FakeBranchService();
        service.Drafts["b1"] = new Queue<BranchSummaryDraft>([new BranchSummaryDraft(null, null, NoModel: true)]);
        var vm = new BranchMergeDialogViewModel(service, OneStep(model: null!));

        await vm.StartAsync();
        Assert.Equal(Strings.Branch_MergeNoModel, vm.ErrorText);
        Assert.False(vm.CanConfirm);
        Assert.False(vm.HasCost);
        Assert.Equal("", vm.CostText);

        vm.SummaryText = "自己寫的摘要";
        Assert.True(vm.CanConfirm);
        vm.Cancel();
        await vm.Completion;
    }

    [Fact]
    public async Task Failure_message_shown()
    {
        var service = new FakeBranchService();
        service.Drafts["b1"] = new Queue<BranchSummaryDraft>([
            new BranchSummaryDraft(null, new ChatFailure(ChatFailureKind.ModelNotConfigured, "連線逾時。", Retryable: true)),
        ]);
        var vm = new BranchMergeDialogViewModel(service, OneStep());

        await vm.StartAsync();

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeFailedFormat, "連線逾時"), vm.ErrorText);
        Assert.True(vm.HasError);
        Assert.False(vm.CanConfirm);
        vm.Cancel();
        await vm.Completion;
    }

    [Fact]
    public async Task Too_long_disables_confirm()
    {
        var service = new FakeBranchService();
        var vm = new BranchMergeDialogViewModel(service, OneStep());
        await vm.StartAsync();

        vm.SummaryText = new string('字', BranchLimits.SummaryMaxChars + 1);
        Assert.False(vm.CanConfirm);
        Assert.True(vm.HasTooLong);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeTooLongFormat,
            BranchLimits.SummaryMaxChars, BranchLimits.SummaryMaxChars + 1), vm.TooLongText);

        vm.SummaryText = new string('字', BranchLimits.SummaryMaxChars);
        Assert.True(vm.CanConfirm);
        Assert.False(vm.HasTooLong);
        vm.Cancel();
        await vm.Completion;
    }

    [Fact]
    public async Task Redraft_replaces_text()
    {
        var service = new FakeBranchService();
        service.Drafts["b1"] = new Queue<BranchSummaryDraft>([
            new BranchSummaryDraft("第一版", null),
            new BranchSummaryDraft("第二版", null),
        ]);
        var vm = new BranchMergeDialogViewModel(service, OneStep());

        await vm.StartAsync();
        Assert.Equal("第一版", vm.SummaryText);
        vm.SummaryText = "自己改過的";
        await vm.RedraftAsync();

        Assert.Equal("第二版", vm.SummaryText);
        vm.Cancel();
        await vm.Completion;
    }

    [Fact]
    public async Task Merge_failure_stays_on_the_same_step()
    {
        var service = new FakeBranchService { ThrowOnMerge = new InvalidOperationException("不是進行中的支線。") };
        var vm = new BranchMergeDialogViewModel(service, TwoSteps());

        await vm.StartAsync();
        await vm.ConfirmAsync();

        Assert.Equal("不是進行中的支線。", vm.ErrorText);
        Assert.False(vm.Completion.IsCompleted);
        Assert.Equal(["Draft:b2", "Merge:b2:摘要:b2"], service.Calls);
        vm.Cancel();
        Assert.Null((await vm.Completion).OpenTo);
    }

    [Fact]
    public async Task Titles_and_step_text_follow_the_step()
    {
        var service = new FakeBranchService();
        var vm = new BranchMergeDialogViewModel(service, TwoSteps());

        Assert.Equal(Strings.Branch_MergeDialogTitleUp, vm.Title);
        Assert.True(vm.ShowStepText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeStepFormat, 1, 2, "第二層", "第一層"), vm.StepText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeExplainFormat, 2), vm.ExplainText);
        Assert.Equal(Strings.Branch_MergeNext, vm.PrimaryText);

        await vm.StartAsync();
        await vm.ConfirmAsync();

        Assert.Equal(Strings.Branch_MergeDialogTitle, vm.Title);
        Assert.Equal(Strings.Branch_Merge, vm.PrimaryText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeExplainFormat, 5), vm.ExplainText);
        vm.Cancel();
        await vm.Completion;
    }

    [Fact]
    public void Cost_text_names_the_model_and_the_main_conversation()
    {
        var vm = new BranchMergeDialogViewModel(new FakeBranchService(), OneStep());

        Assert.True(vm.HasCost);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeCostFormat, "測試 · test-model", "主線"), vm.CostText);
        vm.Cancel();
    }
}
