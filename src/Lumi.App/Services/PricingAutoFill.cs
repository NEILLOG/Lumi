using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Services;

/// <summary>
/// 升級後第一次啟動：還沒有單價的模型設定比對價目表（plan/23-usage-cost.md 決定 14）。
/// 完全相符或正規化相符的帶入單價（<see cref="PriceSource.Suggested"/>）；本機設為免費；其餘（近似、找不到）不動。
/// 不改上下文上限與最大輸出。只做一次：<c>app_meta</c> 的 <see cref="DoneKey"/> 是 <c>1</c> 就略過。
/// </summary>
public static class PricingAutoFill
{
    /// <summary><c>app_meta</c> 裡記「已經比對過」的 key。</summary>
    public const string DoneKey = "pricing_autofill_done";

    public static async Task RunAsync(IModelCatalog catalog, IPriceTableSource prices, IConversationStore meta, CancellationToken ct)
    {
        if (await meta.GetMetaAsync(DoneKey, ct) == "1")
            return;

        foreach (var model in catalog.Current.Models.Where(m => m.Pricing == ModelPricing.None).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var connection = catalog.Current.Connections.FirstOrDefault(c => c.Id == model.ConnectionId);
            if (connection is null)
                continue;

            var match = PriceMatcher.Match(prices.Current, model.ModelId, connection);
            var pricing = match.Kind switch
            {
                PriceMatchKind.Exact or PriceMatchKind.Normalized => PriceMatcher.ToPricing(match.Entry!),
                _ => null,
            };
            if (pricing is null)
                continue;

            await catalog.SaveModelAsync(model with { Pricing = pricing }, ct);
        }

        await meta.SetMetaAsync(DoneKey, "1", ct);
    }
}
