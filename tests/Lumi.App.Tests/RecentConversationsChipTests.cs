using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.App.Tests;

/// <summary>M26 D3c：近況清單是程式自動帶入的內部資料，畫面上不顯示成附件 chip；其他種類的附件照舊。</summary>
public class RecentConversationsChipTests
{
    private static ContextBlock Digest() =>
        new(ContextKind.RecentConversations, "recent_conversations", "", "<recent_conversations>片段</recent_conversations>", Embedded: true);

    private static ContextBlock File(string name) => new(ContextKind.File, name, $"/tmp/{name}", "內容", Embedded: true);

    [Fact]
    public void FromBlocks_RecentConversations_ProducesNoChip()
    {
        var chips = MessageChipViewModel.FromBlocks([Digest()]);

        Assert.Empty(chips);
    }

    [Fact]
    public void FromBlocks_OtherBlocks_StillProduceTheirChips()
    {
        var chips = MessageChipViewModel.FromBlocks([Digest(), File("報告.md"), new ImageBlock("abc.png", "image/png", "截圖.png")]);

        Assert.Equal([ContextKind.File, ContextKind.Image], chips.Select(c => c.Kind));
        Assert.Equal(["報告.md", "截圖.png"], chips.Select(c => c.DisplayName));
    }

    [Fact]
    public void From_ReopenedUserMessage_WithOnlyTheDigest_HasNoAttachments()
    {
        var message = new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("幫我接著做"), Digest()], DateTimeOffset.UtcNow);

        var vm = MessageViewModel.From(message);

        Assert.False(vm.HasAttachments);
        Assert.Empty(vm.Attachments);
    }
}
