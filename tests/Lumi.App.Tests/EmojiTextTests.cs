using Lumi.App.Controls;

namespace Lumi.App.Tests;

public class EmojiTextTests
{
    [Theory]
    [InlineData("❤️‍🔥")]        // ZWJ 組合
    [InlineData("❤️")]          // VS16
    [InlineData("👨‍👩‍👧")]      // 家庭
    [InlineData("1️⃣")]          // 數字鍵帽
    [InlineData("🇹🇼")]          // 國旗
    [InlineData("✅")]          // Emoji_Presentation 的 BMP 字元
    [InlineData("😀")]
    public void IsEmoji_True(string grapheme) => Assert.True(EmojiText.IsEmoji(grapheme));

    [Theory]
    [InlineData("A")]
    [InlineData("中")]
    [InlineData("❤")]           // 沒有 VS16，維持文字樣式
    [InlineData("★")]
    [InlineData("✔")]
    [InlineData("✔︎")]     // VS15：明確要求文字樣式
    public void IsEmoji_False(string grapheme) => Assert.False(EmojiText.IsEmoji(grapheme));
}
