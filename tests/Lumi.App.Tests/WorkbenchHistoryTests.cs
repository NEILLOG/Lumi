using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>9B：歷史抽屜與工作台的整合：刪除目前的對話（導向、草稿、上下文）、改名同步視窗標題、回應中停用。用真的 SQLite 替身（記憶體）。</summary>
public class WorkbenchHistoryTests
{
    private sealed record Rig(WorkbenchViewModel Vm, InMemoryConversationStore Store, FakeConfirmService Confirm);

    private static Rig CreateRig(InMemoryConversationStore? store = null)
    {
        store ??= new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var confirm = new FakeConfirmService();
        var vm = new WorkbenchViewModel(new ConversationService(new ScriptedChatProvider(), store, catalog), store, catalog,
            new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(),
            new SyncUiDispatcher(), new FakeTimeProvider(), NullLogger<WorkbenchViewModel>.Instance,
            deletion: HistoryTestSupport.Deletion(store), confirm: confirm);
        return new Rig(vm, store, confirm);
    }

    /// <summary>建一則有訊息的對話（歷史只列有訊息的）。後建立的更新時間較新，排在清單前面。</summary>
    private static async Task<Conversation> SeedAsync(InMemoryConversationStore store, string title)
    {
        // 清單依更新時間排序：相鄰兩次播種的時間戳不能相同，否則排序順序不固定
        await Task.Delay(5);
        var conversation = await store.CreateAsync(title);
        await store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(title)], DateTimeOffset.UtcNow));
        return conversation;
    }

    /// <summary>像使用者一樣打開抽屜（開啟時會重新載入清單，用的是工作台目前的對話）。</summary>
    private static async Task OpenDrawerAsync(Rig rig, int count)
    {
        rig.Vm.History.IsOpen = true;
        await SettingsHost.WaitUntil(() => rig.Vm.History.Items.Count == count);
    }

    [Fact]
    public async Task DeletingTheCurrent_OpensTheNextInTheList_AndClearsTheDraft()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "較舊的");
        await SeedAsync(store, "較新的（目前）");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await OpenDrawerAsync(rig, 2);
        Assert.Equal("較新的（目前）", rig.Vm.Title);
        rig.Vm.InputText = "還沒送出的草稿";

        await rig.Vm.History.DeleteCommand.ExecuteAsync(rig.Vm.History.Items[0]);

        Assert.Equal("較舊的", rig.Vm.Title);
        Assert.Equal("", rig.Vm.InputText);
        Assert.Equal("較舊的", Assert.Single(rig.Vm.History.Items).Title);
        Assert.Single(rig.Confirm.Asked);
    }

    [Fact]
    public async Task DeletingTheLastOne_OpensANewBlankConversation()
    {
        var store = new InMemoryConversationStore();
        var only = await SeedAsync(store, "唯一的");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await OpenDrawerAsync(rig, 1);
        rig.Vm.InputText = "草稿";

        await rig.Vm.History.DeleteCommand.ExecuteAsync(rig.Vm.History.Items[0]);

        Assert.Empty(rig.Vm.Messages);
        Assert.Equal(Strings.Workbench_NewConversationTitle, rig.Vm.Title);
        Assert.Equal("", rig.Vm.InputText);
        Assert.Empty(rig.Vm.History.Items);   // 空白的新對話不列出
        Assert.DoesNotContain(await store.ListAsync(10), c => c.Id == only.Id);   // 舊的已經刪了
        Assert.Empty(await store.GetMessagesAsync((await store.GetLatestAsync())!.Id));   // 開的是新的空白對話
    }

    [Fact]
    public async Task DeletingANonCurrentConversation_KeepsTheCurrentOneAndItsDraft()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "要刪的");
        await SeedAsync(store, "目前的");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await OpenDrawerAsync(rig, 2);
        rig.Vm.InputText = "草稿";

        var other = rig.Vm.History.Items.Single(i => i.Title == "要刪的");
        await rig.Vm.History.DeleteCommand.ExecuteAsync(other);

        Assert.Equal("目前的", rig.Vm.Title);
        Assert.Equal("草稿", rig.Vm.InputText);
        Assert.Equal(["目前的"], rig.Vm.History.Items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task Rename_OfTheCurrentConversation_UpdatesTheWindowTitle()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "舊名字");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await OpenDrawerAsync(rig, 1);
        var item = rig.Vm.History.Items[0];

        rig.Vm.History.RenameCommand.Execute(item);
        item.EditText = "  新名字  ";
        await rig.Vm.History.CommitRenameCommand.ExecuteAsync(item);

        Assert.Equal("新名字", rig.Vm.Title);
        Assert.Equal("新名字", item.Title);
        Assert.Equal("新名字", (await store.ListAsync(10)).Single().Title);
    }

    [Fact]
    public async Task WhileResponding_TheCurrentConversationCannotBeDeleted_AndTheReasonIsShown()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "回應中");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        await OpenDrawerAsync(rig, 1);
        var item = rig.Vm.History.Items[0];

        rig.Vm.IsResponding = true;

        Assert.False(rig.Vm.History.DeleteCommand.CanExecute(item));
        Assert.True(item.DeleteBlocked);
        Assert.Equal(Strings.History_DeleteBlocked, item.DeleteTip);
        await rig.Vm.History.DeleteCommand.ExecuteAsync(item);
        Assert.Empty(rig.Confirm.Asked);
        Assert.Single(await store.ListAsync(10));

        rig.Vm.IsResponding = false;
        Assert.True(rig.Vm.History.DeleteCommand.CanExecute(item));
        Assert.False(item.DeleteBlocked);
    }

    [Fact]
    public async Task Opening_TheDrawer_RefreshesTheListFromTheStore()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "啟動前就有的");
        var rig = CreateRig(store);
        await rig.Vm.InitializeAsync();
        Assert.Empty(rig.Vm.History.Items);   // 啟動時還沒載入清單

        rig.Vm.History.IsOpen = true;

        await SettingsHost.WaitUntil(() => rig.Vm.History.Items.Count == 1);
        Assert.Equal("啟動前就有的", rig.Vm.History.Items[0].Title);
    }
}
