using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M22 22J：工作台開著小精靈的專屬對話時，「⋯」沒有刪除、改成「清除內容」。</summary>
public class WorkbenchSpriteConversationTests
{
    private sealed record Rig(WorkbenchViewModel Vm, InMemoryConversationStore Store, SpriteConversationService Sprite, FakeConfirmService Confirm);

    private static Rig CreateRig(IChatProvider? provider = null)
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var catalog = new FakeModelCatalog();
        var confirm = new FakeConfirmService();
        var service = new ConversationService(provider ?? new ScriptedChatProvider(), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, deletion: HistoryTestSupport.Deletion(store), confirm: confirm,
            session: new ChatSession(service, store, sprite: sprite), spriteConversation: sprite);
        return new Rig(vm, store, sprite, confirm);
    }

    private static async Task<Conversation> SeedAsync(InMemoryConversationStore store, string title, bool sprite = false, SpriteConversationService? service = null)
    {
        var conversation = sprite ? await service!.GetOrCreateAsync(title) : await store.CreateAsync(title);
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("內容")], DateTimeOffset.UtcNow));
        return conversation;
    }

    private static Task OpenAsync(Rig rig, Conversation conversation) => rig.Vm.OpenConversationAsync(conversation);

    [Fact]
    public async Task SpriteConversation_HasClearInsteadOfDelete()
    {
        var rig = CreateRig();
        await rig.Vm.InitializeAsync();
        var sprite = await SeedAsync(rig.Store, "與小精靈的對話", sprite: true, rig.Sprite);

        await OpenAsync(rig, sprite);

        Assert.True(rig.Vm.IsSpriteConversation);
        Assert.False(rig.Vm.ShowDeleteConversation);
        Assert.False(rig.Vm.DeleteConversationEnabled);
        Assert.True(rig.Vm.ClearConversationEnabled);
        Assert.False(rig.Vm.DeleteConversationCommand.CanExecute(null));
    }

    [Fact]
    public async Task OrdinaryConversation_HasDeleteAndNoClear()
    {
        var rig = CreateRig();
        await rig.Vm.InitializeAsync();
        var ordinary = await SeedAsync(rig.Store, "一般對話");

        await OpenAsync(rig, ordinary);

        Assert.False(rig.Vm.IsSpriteConversation);
        Assert.True(rig.Vm.ShowDeleteConversation);
        Assert.True(rig.Vm.DeleteConversationEnabled);
        Assert.False(rig.Vm.ClearConversationEnabled);
    }

    [Fact]
    public async Task Clear_AfterConfirming_EmptiesTheConversation_KeepingItsIdAndTitle()
    {
        var rig = CreateRig();
        await rig.Vm.InitializeAsync();
        var sprite = await SeedAsync(rig.Store, "與小精靈的對話", sprite: true, rig.Sprite);
        await OpenAsync(rig, sprite);
        Assert.NotEmpty(rig.Vm.Messages);

        await rig.Vm.ClearConversationCommand.ExecuteAsync(null);

        Assert.Equal([(Strings.Confirm_ClearSpriteConversation, Strings.Confirm_Clear)], rig.Confirm.Asked);
        Assert.Empty(rig.Vm.Messages);
        Assert.Empty(await rig.Store.GetMessagesAsync(sprite.Id));
        Assert.Equal("與小精靈的對話", rig.Vm.Title);
        Assert.Equal(sprite.Id, await rig.Sprite.GetIdAsync());
        Assert.True(rig.Vm.IsSpriteConversation);
        Assert.False(rig.Vm.ClearConversationEnabled);   // 已經是空的
    }

    [Fact]
    public async Task Clear_WhenNotConfirmed_KeepsEverything()
    {
        var rig = CreateRig();
        await rig.Vm.InitializeAsync();
        var sprite = await SeedAsync(rig.Store, "與小精靈的對話", sprite: true, rig.Sprite);
        await OpenAsync(rig, sprite);
        rig.Confirm.Answer = false;

        await rig.Vm.ClearConversationCommand.ExecuteAsync(null);

        Assert.NotEmpty(rig.Vm.Messages);
        Assert.Single(await rig.Store.GetMessagesAsync(sprite.Id));
    }

    [Fact]
    public async Task Clear_IsDisabledWhileResponding()
    {
        var rig = CreateRig(new ScriptedChatProvider(new TextDelta("…")) { HangAfter = 1 });
        await rig.Vm.InitializeAsync();
        var sprite = await SeedAsync(rig.Store, "與小精靈的對話", sprite: true, rig.Sprite);
        await OpenAsync(rig, sprite);
        rig.Vm.InputText = "嗨";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);

        Assert.False(rig.Vm.ClearConversationEnabled);
        Assert.False(rig.Vm.ClearConversationCommand.CanExecute(null));

        rig.Vm.StopCommand.Execute(null);
        await sending;
    }

    [Fact]
    public async Task NewConversation_OnAnEmptySpriteConversation_StillCreatesAWorkbenchConversation()
    {
        var rig = CreateRig();
        await rig.Vm.InitializeAsync();
        var sprite = await rig.Sprite.GetOrCreateAsync("與小精靈的對話");   // 空的，還沒有任何訊息
        await OpenAsync(rig, sprite);
        Assert.Empty(rig.Vm.Messages);

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.IsSpriteConversation);
        Assert.Equal(Strings.Workbench_NewConversationTitle, rig.Vm.Title);
    }

    [Fact]
    public async Task StartupInitialize_DoesNotOpenTheSpriteConversation()
    {
        var rig = CreateRig();
        var work = await SeedAsync(rig.Store, "工作台的對話");
        await Task.Delay(3);
        await SeedAsync(rig.Store, "與小精靈的對話", sprite: true, rig.Sprite);   // 小精靈的對話較新

        await rig.Vm.InitializeAsync();

        Assert.False(rig.Vm.IsSpriteConversation);
        Assert.Equal("工作台的對話", rig.Vm.Title);
        Assert.NotNull(work);
    }
}
