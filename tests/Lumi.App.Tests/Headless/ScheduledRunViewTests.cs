using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>M24 24F：排程紀錄檢視在工作台裡的版面（橫幅、說明列、訊息區、即時卡片）能建出來、狀態正確。</summary>
public class ScheduledRunViewTests
{
    private static (WorkbenchWindow Window, WorkbenchViewModel Vm, InMemoryScheduledRunStore Runs, FakeScheduledRunService Service, InMemoryConversationStore Store) Show()
    {
        var store = new InMemoryConversationStore();
        var runs = new InMemoryScheduledRunStore();
        var service = new FakeScheduledRunService();
        var catalog = new FakeModelCatalog();
        var conversations = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(conversations, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, scheduledRuns: service, runStore: runs);
        var window = new WorkbenchWindow(vm) { Width = 1200 };
        window.Show();
        window.UpdateLayout();
        return (window, vm, runs, service, store);
    }

    /// <summary>一則排程紀錄（有對話的會先存一則使用者訊息與一則回答）。</summary>
    private static async Task<ScheduledRun> SeedAsync(InMemoryConversationStore store, InMemoryScheduledRunStore runs, ScheduledRunStatus status)
    {
        string? conversationId = null;
        if (status != ScheduledRunStatus.Missed)
        {
            var conversation = await store.CreateAsync("每日摘要");
            conversationId = conversation.Id;
            var now = DateTimeOffset.UtcNow;
            await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new Lumi.Core.Chat.TextBlock("整理新聞")], now));
            await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new Lumi.Core.Chat.TextBlock("整理好了")], now));
        }
        var run = new ScheduledRun(ScheduledRun.NewId(), "s1", "每日摘要", conversationId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            status is ScheduledRunStatus.Running ? null : DateTimeOffset.UtcNow, ScheduledRunTrigger.Scheduled, status, "出錯了", null);
        await runs.AddAsync(run);
        return run;
    }

    [AvaloniaFact]
    public async Task RunView_ShowsTheBanner_AndTheNoteBar_InsteadOfTheComposer()
    {
        var (window, vm, runs, _, store) = Show();
        var run = await SeedAsync(store, runs, ScheduledRunStatus.Completed);

        await vm.OpenScheduledRunAsync(run.Id);
        window.UpdateLayout();

        Assert.True(window.FindByName<Border>("RunViewBanner").IsVisible);
        Assert.True(window.FindByName<Border>("RunViewBar").IsVisible);
        Assert.False(window.FindByName<Border>("InputArea").IsVisible);
        Assert.True(window.FindByName<Button>("ContinueChatButton").IsVisible);
        Assert.False(window.FindByName<Button>("RunAgainButton").IsVisible);
        Assert.False(window.FindByName<Button>("StopRunButton").IsVisible);
        Assert.True(window.FindByName<ConversationView>("Conversation").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RunView_Missed_OffersRunAgain_AndNoContinue()
    {
        var (window, vm, runs, _, store) = Show();
        var run = await SeedAsync(store, runs, ScheduledRunStatus.Missed);

        await vm.OpenScheduledRunAsync(run.Id);
        window.UpdateLayout();

        Assert.True(window.FindByName<Button>("RunAgainButton").IsVisible);
        Assert.False(window.FindByName<Button>("ContinueChatButton").IsVisible);
        Assert.True(window.FindByName<Border>("RunViewBar").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RunView_Running_ShowsStop_AndLiveToolCards_WithoutErrors()
    {
        var (window, vm, runs, service, store) = Show();
        var run = await SeedAsync(store, runs, ScheduledRunStatus.Running);

        await vm.OpenScheduledRunAsync(run.Id);
        service.Raise(new ScheduledRunEventArgs(run.Id, run.ConversationId!, new ToolCallStarted("call-1", "web_search", "搜尋網頁", ToolPermission.ReadOnly, "新聞", "{}")));
        service.Raise(new ScheduledRunEventArgs(run.Id, run.ConversationId!, new TextDelta("整理中")));
        window.UpdateLayout();

        Assert.True(window.FindByName<Button>("StopRunButton").IsVisible);
        Assert.True(window.FindByName<Border>("RunViewBanner").IsVisible);
        Assert.NotEmpty(window.FindByName<ConversationView>("Conversation").GetVisualDescendants().OfType<ToolRunView>());
        window.Close();
    }

    [AvaloniaFact]
    public async Task RunView_BackToChat_RestoresTheComposer()
    {
        var (window, vm, runs, _, store) = Show();
        var run = await SeedAsync(store, runs, ScheduledRunStatus.Completed);
        await vm.OpenScheduledRunAsync(run.Id);
        window.UpdateLayout();

        vm.BackToChatCommand.Execute(null);
        window.UpdateLayout();

        Assert.False(window.FindByName<Border>("RunViewBanner").IsVisible);
        Assert.False(window.FindByName<Border>("RunViewBar").IsVisible);
        Assert.True(window.FindByName<Border>("InputArea").IsVisible);
        window.Close();
    }
}
