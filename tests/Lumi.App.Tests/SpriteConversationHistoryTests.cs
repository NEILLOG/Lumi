using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;

namespace Lumi.App.Tests;

/// <summary>M22 22J：歷史清單裡的小精靈專屬對話——固定在最上方、不分頁、搜尋符合才出現、沒有刪除只有「清除內容」。</summary>
public class SpriteConversationHistoryTests
{
    private sealed record Rig(
        HistoryViewModel Vm, InMemoryConversationStore Store, SpriteConversationService Sprite, FakeHistoryHost Host,
        FakeConfirmService Confirm);

    private static Rig CreateRig(int pageSize = 50, bool responding = false)
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var host = new FakeHistoryHost { IsResponding = responding };
        var confirm = new FakeConfirmService();
        var vm = new HistoryViewModel(store, host, new ManualTimeProvider(DateTimeOffset.UtcNow),
            new HistoryOptions { PageSize = pageSize, SearchDebounceMilliseconds = 0 },
            HistoryTestSupport.Deletion(store), confirm, sprite: sprite);
        return new Rig(vm, store, sprite, host, confirm);
    }

    private static async Task<Conversation> SeedAsync(InMemoryConversationStore store, string title, string text = "內容")
    {
        await Task.Delay(3);   // 清單依更新時間排序：相鄰兩次播種的時間戳不能相同
        var conversation = await store.CreateAsync(title);
        await store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow));
        return conversation;
    }

    private static async Task<Conversation> SeedSpriteAsync(Rig rig, string text = "嗨")
    {
        var conversation = await rig.Sprite.GetOrCreateAsync(Strings.Sprite_ConversationTitle);
        await rig.Store.AppendMessageAsync(conversation.Id,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow));
        return conversation;
    }

    [Fact]
    public async Task WhenItHasNotBeenCreated_NothingIsPinned()
    {
        var rig = CreateRig();
        await SeedAsync(rig.Store, "一般對話");

        await rig.Vm.RefreshAsync(null);

        Assert.DoesNotContain(rig.Vm.Items, i => i.IsSprite);
        Assert.Single(rig.Vm.Items);
    }

    [Fact]
    public async Task ItIsPinnedAtTheTop_EvenWhenOlderThanEveryOtherConversation()
    {
        var rig = CreateRig();
        var sprite = await SeedSpriteAsync(rig);   // 最舊
        await SeedAsync(rig.Store, "甲");
        await SeedAsync(rig.Store, "乙");

        await rig.Vm.RefreshAsync(null);

        var first = Assert.IsType<ConversationListItem>(rig.Vm.Rows[0]);   // 第一列就是它，前面沒有群組標題
        Assert.Equal(sprite.Id, first.Id);
        Assert.True(first.IsSprite);
        Assert.Equal(Strings.History_SpriteTag, first.SpriteTagText);
        Assert.Equal(["乙", "甲"], rig.Vm.Items.Where(i => !i.IsSprite).Select(i => i.Title));
        Assert.Single(rig.Vm.Rows.OfType<ConversationListItem>(), i => i.Id == sprite.Id);
    }

    [Fact]
    public async Task EvenWhenItIsTheNewest_ItAppearsOnlyOnce_AtTheTop()
    {
        var rig = CreateRig();
        await SeedAsync(rig.Store, "甲");
        await Task.Delay(3);
        var sprite = await SeedSpriteAsync(rig);   // 最新

        await rig.Vm.RefreshAsync(null);

        Assert.Equal(sprite.Id, ((ConversationListItem)rig.Vm.Rows[0]).Id);
        Assert.Single(rig.Vm.Items, i => i.Id == sprite.Id);
    }

    [Fact]
    public async Task Paging_DoesNotCountOrRepeatIt()
    {
        var rig = CreateRig(pageSize: 2);
        var sprite = await SeedSpriteAsync(rig);   // 最舊：在最後一頁
        for (var i = 1; i <= 4; i++)
            await SeedAsync(rig.Store, $"對話{i}");

        await rig.Vm.RefreshAsync(null);
        Assert.Equal(sprite.Id, rig.Vm.Items[0].Id);
        Assert.Equal(["對話4", "對話3"], rig.Vm.Items.Where(i => !i.IsSprite).Select(i => i.Title));
        Assert.True(rig.Vm.HasMore);

        await rig.Vm.LoadMoreCommand.ExecuteAsync(null);
        await rig.Vm.LoadMoreCommand.ExecuteAsync(null);

        Assert.Equal(5, rig.Vm.Items.Count);
        Assert.Single(rig.Vm.Items, i => i.Id == sprite.Id);
        Assert.Equal(sprite.Id, rig.Vm.Items[0].Id);
    }

    [Fact]
    public async Task Search_ShowsItOnlyWhenItMatches_AndThenStillAtTheTop()
    {
        var rig = CreateRig();
        await SeedAsync(rig.Store, "蘋果派食譜");
        var sprite = await SeedSpriteAsync(rig, "今天想吃蘋果");
        await SeedAsync(rig.Store, "旅遊計畫");
        await rig.Vm.RefreshAsync(null);

        rig.Vm.SearchText = "蘋果";
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching);

        Assert.Equal(sprite.Id, rig.Vm.Items[0].Id);
        Assert.True(rig.Vm.Items[0].IsSprite);
        Assert.Equal(2, rig.Vm.Items.Count);

        rig.Vm.SearchText = "旅遊";
        await SettingsHost.WaitUntil(() => !rig.Vm.IsSearching && rig.Vm.Items.Any(i => i.Title == "旅遊計畫"));

        Assert.DoesNotContain(rig.Vm.Items, i => i.IsSprite);
    }

    [Fact]
    public async Task ItCannotBeDeleted_FromTheListOrThroughTheWorkbenchMenu()
    {
        var rig = CreateRig();
        var sprite = await SeedSpriteAsync(rig);
        await rig.Vm.RefreshAsync(null);
        var item = rig.Vm.Items[0];

        Assert.False(rig.Vm.DeleteCommand.CanExecute(item));
        await rig.Vm.DeleteConversationAsync(sprite.Id, sprite.Title);

        Assert.Empty(rig.Confirm.Asked);
        Assert.NotNull(await rig.Store.GetConversationAsync(sprite.Id));
        Assert.Empty(rig.Host.Deleted);
    }

    [Fact]
    public async Task Clear_AfterConfirming_RemovesTheContent_KeepsTheConversation_AndTellsTheHost()
    {
        var rig = CreateRig();
        var sprite = await SeedSpriteAsync(rig);
        await rig.Vm.RefreshAsync(null);
        var item = rig.Vm.Items[0];
        Assert.True(rig.Vm.ClearSpriteConversationCommand.CanExecute(item));

        await rig.Vm.ClearSpriteConversationCommand.ExecuteAsync(item);

        Assert.Equal([(Strings.Confirm_ClearSpriteConversation, Strings.Confirm_Clear)], rig.Confirm.Asked);
        Assert.Empty(await rig.Store.GetMessagesAsync(sprite.Id));
        Assert.Equal(sprite.Title, (await rig.Store.GetConversationAsync(sprite.Id))?.Title);
        Assert.Equal(sprite.Id, await rig.Sprite.GetIdAsync());
        Assert.Equal([sprite.Id], rig.Host.Cleared);
    }

    [Fact]
    public async Task Clear_WhenNotConfirmed_DoesNothing()
    {
        var rig = CreateRig();
        var sprite = await SeedSpriteAsync(rig);
        await rig.Vm.RefreshAsync(null);
        rig.Confirm.Answer = false;

        await rig.Vm.ClearSpriteConversationCommand.ExecuteAsync(rig.Vm.Items[0]);

        Assert.Single(await rig.Store.GetMessagesAsync(sprite.Id));
        Assert.Empty(rig.Host.Cleared);
    }

    [Fact]
    public async Task Clear_IsBlockedWhileResponding_AndForOrdinaryConversations()
    {
        var rig = CreateRig(responding: true);
        var sprite = await SeedSpriteAsync(rig);
        var ordinary = await SeedAsync(rig.Store, "一般對話");
        await rig.Vm.RefreshAsync(null);
        var spriteItem = rig.Vm.Items.Single(i => i.Id == sprite.Id);
        var ordinaryItem = rig.Vm.Items.Single(i => i.Id == ordinary.Id);

        Assert.False(rig.Vm.ClearSpriteConversationCommand.CanExecute(spriteItem));   // 回應中
        await rig.Vm.ClearConversationAsync(sprite.Id);
        Assert.Single(await rig.Store.GetMessagesAsync(sprite.Id));

        rig.Host.IsResponding = false;
        rig.Vm.HostStateChanged();
        Assert.True(rig.Vm.ClearSpriteConversationCommand.CanExecute(spriteItem));
        Assert.False(rig.Vm.ClearSpriteConversationCommand.CanExecute(ordinaryItem));   // 一般對話沒有「清除內容」
        await rig.Vm.ClearConversationAsync(ordinary.Id);
        Assert.Single(await rig.Store.GetMessagesAsync(ordinary.Id));
    }

    [Fact]
    public async Task RenamingTheSpriteConversation_StillWorks()
    {
        var rig = CreateRig();
        var sprite = await SeedSpriteAsync(rig);
        await rig.Vm.RefreshAsync(null);

        Assert.True(await rig.Vm.RenameConversationAsync(sprite.Id, "我的小幫手"));

        Assert.Equal("我的小幫手", (await rig.Store.GetConversationAsync(sprite.Id))?.Title);
        Assert.Equal("我的小幫手", rig.Vm.Items[0].Title);
    }
}
