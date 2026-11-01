namespace Lumi.Tarot;

/// <summary>讀 TarotMcp.Core 內嵌的牌面圖片（CardAssets）。</summary>
public sealed class TarotCardImages : ITarotCardImages
{
    public static ITarotCardImages Default { get; } = new TarotCardImages();

    // TODO(25A)：實作；Phase 0 的空殼一律回 null（畫面走替代牌面）
    public Stream? OpenCard(string cardId) => null;

    public Stream? OpenFrame() => null;

    public TarotWindow? FrameWindow => null;
}
