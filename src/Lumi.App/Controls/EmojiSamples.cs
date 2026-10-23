namespace Lumi.App.Controls;

/// <summary>
/// 9E 的 emoji 驗證字串（plan/09-history-polish.md「Emoji（9E）」）。
/// 給單元測試（tests/Lumi.App.Tests/EmojiSegmentationTests.cs）與驗證頁（poc/Lumi.EmojiPoc）共用；不是產品功能。
/// 改這裡的字串時，要同步更新 poc/Lumi.EmojiPoc/README.md 的驗證清單。
/// </summary>
public static class EmojiSamples
{
    /// <summary>一組驗證字串：名稱（驗證頁與 README 用）與內容。空白只是分隔，組合在空白之間。</summary>
    public sealed record Set(string Name, string Text);

    public static IReadOnlyList<Set> Sets { get; } =
    [
        new("國旗", "🇹🇼🇯🇵🇺🇸🇬🇧"),
        new("膚色", "👍🏽 👋🏿"),
        new("ZWJ 組合", "👨‍👩‍👧 🏳️‍🌈 ❤️‍🔥"),
        new("鍵帽", "1️⃣ #️⃣"),
        new("VS16 符號", "☀️ ✈️"),
        new("Unicode 15+", "🫨 🪿 🫎"),
        new("混合句子", "今天的天氣☀️很好 🇹🇼 Hello, world! 👋🏿 ❤️‍🔥 好嗎？"),
    ];
}
