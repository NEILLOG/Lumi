using Microsoft.Extensions.Logging;

namespace Lumi.Core.Usage;

/// <summary>
/// 把每次供應商呼叫的用量寫進 <see cref="IUsageStore"/>（M23 決定 1、2、9）。
/// 單價在這裡拍快照：之後改模型設定的單價，不影響已經寫入的紀錄。
/// 任何例外只記 log、不往外丟：記錄失敗不能讓對話失敗。
/// </summary>
public sealed class UsageRecorder(IUsageStore store, TimeProvider time, ILogger<UsageRecorder> logger) : IUsageRecorder
{
    public async Task RecordAsync(UsageObservation observation, CancellationToken ct = default)
    {
        try
        {
            var model = observation.Model;
            var pricing = model.Model.Pricing;
            var price = PriceSnapshot.From(pricing);
            var record = new UsageRecord(
                UsageRecord.NewId(),
                time.GetUtcNow(),
                observation.ConversationId,
                observation.Purpose,
                observation.Mode,
                model.Model.Id,
                model.Model.ModelId,
                model.Connection.Name,
                observation.Tokens,
                observation.Estimated,
                price,
                CostCalculator.Cost(observation.Tokens, price),
                observation.ToolDefinitionTokens);
            await store.AddAsync(record, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 不記內容（對話與提示都不進 log）；這次請求只是不計入用量
            logger.LogWarning(ex, "記錄用量失敗，這次請求沒有計入用量。");
        }
    }
}
