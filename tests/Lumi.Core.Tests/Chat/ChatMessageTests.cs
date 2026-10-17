using Lumi.Core.Chat;

namespace Lumi.Core.Tests.Chat;

public class ChatMessageTests
{
    [Fact]
    public void NewId_values_are_unique()
    {
        Assert.NotEqual(ChatMessage.NewId(), ChatMessage.NewId());
    }

    [Fact]
    public async Task NewId_values_sort_by_creation_order_at_millisecond_resolution()
    {
        // Guid v7 的排序只保證到毫秒級的時間戳記；同一毫秒內連續呼叫兩次，剩下的位元是隨機的，
        // 誰先誰後沒有保證（这不是 flaky，是 v7 的設計）。1D 用它當 SQLite 的排序鍵，
        // 要的是「大致照時間排序」，不是微秒級的嚴格遞增，所以測試之間隔一點時間。
        var a = ChatMessage.NewId();
        await Task.Delay(5);
        var b = ChatMessage.NewId();

        Assert.True(string.CompareOrdinal(a, b) <= 0);
    }

    [Fact]
    public void Conversation_NewId_is_also_unique()
    {
        Assert.NotEqual(Conversation.NewId(), Conversation.NewId());
    }
}
