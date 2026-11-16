using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Branches;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>
/// M36 端對端：真的 <see cref="ConversationService"/>＋<see cref="BranchService"/>＋記憶體儲存，從工作台開支線、送一輪、帶回、主線再送一輪。
/// 主線第二次的請求要帶著支線摘要，摘要卡先在最後面，交出之後移到那則使用者訊息上方。
/// </summary>
public class BranchEndToEndTests
{
    private sealed class FixedSummarizer : IBranchSummarizer
    {
        public Task<BranchSummaryDraft> SummarizeAsync(BranchSummaryRequest request, CancellationToken ct = default) =>
            Task.FromResult(new BranchSummaryDraft("草稿", null));
    }

    [Fact]
    public async Task StartBranch_SendMergeAndSendInMain_DeliversTheSummaryOnce()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.Answer("主線的回答"),
            SequenceChatProvider.Answer("支線的回答"),
            SequenceChatProvider.Answer("主線的第二個回答"),
            SequenceChatProvider.Answer("主線的第三個回答"));
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var branchStore = new InMemoryBranchStore();
        var service = new ConversationService(provider, store, catalog, new ToolRegistry([]), new DenyAllToolApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            branches: branchStore);
        var branches = new BranchService(store, branchStore, new InMemoryConversationContextStore(), new FixedSummarizer(),
            new SpriteConversationService(store), TimeProvider.System);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, branches: branches, branchStore: branchStore, mergePrompt: new FakeBranchMergePrompt());
        await vm.InitializeAsync();

        // 主線一輪
        vm.InputText = "主線的問題";
        await vm.SendCommand.ExecuteAsync(null);
        var main = (await store.GetLatestAsync())!;
        var answer = vm.Messages.Last(m => m.IsAssistant && !m.IsMarker);
        Assert.True(answer.ShowBranchAction);
        Assert.True(answer.CanBranch);

        // 開支線、送一輪
        await vm.StartBranchCommand.ExecuteAsync(answer);
        Assert.True(vm.IsInBranch);
        Assert.IsType<BranchOriginViewModel>(vm.Messages[0]);
        vm.InputText = "支線的問題";
        await vm.SendCommand.ExecuteAsync(null);
        var branch = Assert.Single(await branchStore.ListChildrenAsync(main.Id));
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].Messages, m => m.Content.OfType<ContextBlock>().Any(b => b.Kind == ContextKind.BranchOrigin));

        // 帶回（對話框由服務代勞）並回到主線：摘要卡在最後面，還沒交出
        var parent = await branches.MergeAsync(branch.ConversationId, "弄懂了支線的事");
        Assert.Equal(main.Id, parent.Id);
        await vm.OpenBranchCommand.ExecuteAsync(parent.Id);
        Assert.False(vm.IsInBranch);
        var pending = Assert.IsType<BranchSummaryCardViewModel>(vm.Messages[^1]);
        Assert.True(pending.IsPending);

        // 主線送下一則：請求帶著摘要，卡片移到那則使用者訊息上方
        vm.InputText = "接著問主線";
        await vm.SendCommand.ExecuteAsync(null);
        var lastUser = provider.Requests[2].Messages.Last(m => m.Role == ChatRole.User);
        var summaryBlock = Assert.Single(lastUser.Content.OfType<ContextBlock>(), b => b.Kind == ContextKind.BranchSummary);
        Assert.Contains("<branch_summary", ContextBlockFormatter.ToPromptText(summaryBlock), StringComparison.Ordinal);
        Assert.Contains("弄懂了支線的事", ContextBlockFormatter.ToPromptText(summaryBlock), StringComparison.Ordinal);
        var cardIndex = vm.Messages.ToList().FindIndex(m => m is BranchSummaryCardViewModel);
        Assert.True(cardIndex >= 0);
        Assert.False(((BranchSummaryCardViewModel)vm.Messages[cardIndex]).IsPending);
        Assert.True(vm.Messages[cardIndex + 1].IsUser);

        // 再送一則：摘要只交出一次
        vm.InputText = "再問一次";
        await vm.SendCommand.ExecuteAsync(null);
        var fourth = provider.Requests[3].Messages.Last(m => m.Role == ChatRole.User);
        Assert.DoesNotContain(fourth.Content, c => c is ContextBlock { Kind: ContextKind.BranchSummary });
    }
}
