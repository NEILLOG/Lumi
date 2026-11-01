using TarotMcp.Core;
using TarotMcp.Core.Randomness;

namespace Lumi.Tarot;

/// <summary>第一次用到時才載入並驗證牌組；驗證失敗只讓塔羅工具回錯誤，不影響程式啟動（D12）。</summary>
public sealed class TarotDeck
{
    private readonly Lazy<DeckData> _deck;
    private readonly Lazy<DrawService> _draw;

    public TarotDeck(Func<DeckData> load, IRandomSource random)
    {
        _deck = new Lazy<DeckData>(load);
        _draw = new Lazy<DrawService>(() => new DrawService(_deck.Value, random));
    }

    /// <exception cref="DeckValidationException">牌組資料驗證失敗。</exception>
    public DeckData Deck => _deck.Value;

    /// <exception cref="DeckValidationException">牌組資料驗證失敗。</exception>
    public DrawService Draw => _draw.Value;
}
