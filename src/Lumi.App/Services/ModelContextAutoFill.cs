using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Services;

/// <summary>
/// 既有模型的上下文上限跟著價目表調高：新增模型時留下的預設值（OpenAI 相容連線是 128K）通常比模型實際的上限小，
/// 用量顯示的百分比與整理對話的時機都會偏早。每次啟動、以及更新價目表之後各跑一次，靠下面的條件冪等（沒有「只做一次」的旗標）。
/// 全部成立才更新：連線是 OpenAI 相容格式、上限還是預設值（視為使用者沒改過）、不在地端名單
/// （地端部署的上限不等於價目表）、價目表完全相符或正規化相符（近似與候選不算）、表內的值比目前的大（只調高不調低）。
/// 只改 <see cref="ModelSettings.ContextWindow"/>，不動 <see cref="ModelSettings.MaxOutputTokens"/>：
/// 後者會直接送給 API，改了可能讓原本能用的模型收到 400。
/// </summary>
public static class ModelContextAutoFill
{
    public static async Task RunAsync(IModelCatalog catalog, IPriceTableSource prices, CancellationToken ct)
    {
        var defaultContext = ModelsViewModel.DefaultsFor(ProviderFormat.OpenAICompatible).Context;

        foreach (var model in catalog.Current.Models.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (model.ContextWindow != defaultContext || LocalModelList.IsLocalModel(model.ModelId))
                continue;
            var connection = catalog.Current.Connections.FirstOrDefault(c => c.Id == model.ConnectionId);
            if (connection is not { Format: ProviderFormat.OpenAICompatible })
                continue;

            var match = PriceMatcher.Match(prices.Current, model.ModelId, connection);
            if (match.Kind is not (PriceMatchKind.Exact or PriceMatchKind.Normalized))
                continue;
            if (match.Entry!.MaxInputTokens is not { } context || context <= model.ContextWindow)
                continue;

            await catalog.SaveModelAsync(model with { ContextWindow = context }, ct);
        }
    }
}
