using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using TarotMcp.Core;
using TarotMcp.Core.Randomness;

namespace Lumi.Tarot;

public static class TarotServiceCollectionExtensions
{
    /// <summary>
    /// 註冊塔羅牌：牌組（延後載入，D12）與兩個工具（tarot_draw、tarot_card）。
    /// 圖片不經 DI：畫面直接用 <see cref="TarotCardImages.Default"/>。
    /// </summary>
    public static IServiceCollection AddLumiTarot(this IServiceCollection services)
    {
        // 傳方法群組，不要呼叫 LoadEmbedded()：要等第一次用到塔羅工具才載入
        services.AddSingleton(new TarotDeck(DeckData.LoadEmbedded, new SecureRandomSource()));
        services.AddLumiTool<TarotDrawTool>();
        services.AddLumiTool<TarotCardTool>();
        return services;
    }
}
