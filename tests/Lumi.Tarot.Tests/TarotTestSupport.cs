using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using TarotMcp.Core;
using TarotMcp.Core.Randomness;

namespace Lumi.Tarot.Tests;

/// <summary>塔羅測試共用的替身與工具。亂數一律用固定種子，結果可重現。</summary>
internal static class TarotTestSupport
{
    public static readonly ToolContext Context = new("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    /// <summary>正常載入內嵌牌組、固定種子的牌組。</summary>
    public static TarotDeck SeededDeck(int seed = 42) => new(DeckData.LoadEmbedded, new SeededRandomSource(seed));

    /// <summary>載入時一定失敗的牌組（D12）。</summary>
    public static TarotDeck BrokenDeck() => new(() => throw new DeckValidationException(["測試"]), new SeededRandomSource(1));

    public static Stream Text(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));
}
