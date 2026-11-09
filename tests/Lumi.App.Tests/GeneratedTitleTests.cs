using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>背景產生的標題存好之後：開著的對話換掉頂端標題，歷史清單那一列也換。</summary>
public class GeneratedTitleTests
{
    private sealed class FakeTitler : IConversationTitler
    {
        public event EventHandler<ConversationTitleGenerated>? TitleGenerated;

        public Task GenerateAsync(string conversationId, string userText, string fallbackTitle, ChatMode mode, CancellationToken ct = default) =>
            Task.CompletedTask;

        public void Raise(string id, string title) => TitleGenerated?.Invoke(this, new ConversationTitleGenerated(id, title));
    }

    /// <summary>空的對話不會列在清單裡，所以先放一則訊息。</summary>
    private static async Task<Conversation> SeedAsync(InMemoryConversationStore store, string title)
    {
        var conversation = await store.CreateAsync(title);
        await store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("嗨")], DateTimeOffset.UtcNow));
        return conversation;
    }

    [Fact]
    public async Task GeneratedTitle_UpdatesTheTopBarAndTheHistoryRow()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "幫我把上週三的產品會議錄音整理成會議紀錄");
        var catalog = new FakeModelCatalog();
        var titler = new FakeTitler();
        var service = new ConversationService(new SequenceChatProvider(), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, titler: titler);
        await vm.InitializeAsync();
        var id = (await store.GetLatestAsync())!.Id;
        await vm.History.RefreshAsync(id);

        titler.Raise(id, "產品會議紀錄整理");

        Assert.Equal("產品會議紀錄整理", vm.Title);
        Assert.Equal("產品會議紀錄整理", vm.History.Items.Single(i => i.Id == id).Title);
    }

    [Fact]
    public async Task GeneratedTitle_ForAnotherConversation_OnlyUpdatesItsRow()
    {
        var store = new InMemoryConversationStore();
        var other = await SeedAsync(store, "另一則");
        var catalog = new FakeModelCatalog();
        var titler = new FakeTitler();
        var service = new ConversationService(new SequenceChatProvider(), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, titler: titler);
        await vm.InitializeAsync();
        await vm.NewConversationCommand.ExecuteAsync(null);
        await vm.History.RefreshAsync(null);
        var before = vm.Title;

        titler.Raise(other.Id, "換好的標題");

        Assert.Equal(before, vm.Title);
        Assert.Equal("換好的標題", vm.History.Items.Single(i => i.Id == other.Id).Title);
    }
}
