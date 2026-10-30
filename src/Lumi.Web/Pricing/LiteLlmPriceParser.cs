using System.Text.Json;
using Lumi.Core.Usage;

namespace Lumi.Web.Pricing;

/// <summary>
/// 解析 LiteLLM 的 <c>model_prices_and_context_window.json</c>（plan 第 4 節「解析」）。只收聊天模型且有輸入與輸出價的條目；
/// 單一條目讀不懂就略過它，不讓整份失敗。整份不是 JSON 物件時丟 <see cref="JsonException"/>。
/// </summary>
internal static class LiteLlmPriceParser
{
    private const decimal TokensPerMillion = 1_000_000m;
    private const int PriceDecimals = 4;
    private const string SampleKey = "sample_spec";

    public static PriceTable Parse(Stream json, DateOnly snapshotDate)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("價目表的格式不正確。");

        var entries = new List<PriceEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == SampleKey || seen.Contains(property.Name))
                continue;
            if (ParseEntry(property.Name, property.Value) is { } entry)
            {
                entries.Add(entry);
                seen.Add(property.Name);
            }
        }

        return new PriceTable(entries, snapshotDate);
    }

    private static PriceEntry? ParseEntry(string key, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        if (value.TryGetProperty("mode", out var mode) && !(mode.ValueKind == JsonValueKind.String && mode.GetString() == "chat"))
            return null;

        if (!TryReadPerMillion(value, "input_cost_per_token", out var input) || input is null)
            return null;
        if (!TryReadPerMillion(value, "output_cost_per_token", out var output) || output is null)
            return null;
        if (!TryReadPerMillion(value, "cache_read_input_token_cost", out var cacheRead))
            return null;
        if (!TryReadPerMillion(value, "cache_creation_input_token_cost", out var cacheWrite))
            return null;
        if (!TryReadTokens(value, "max_input_tokens", out var maxInput))
            return null;
        if (!TryReadTokens(value, "max_output_tokens", out var maxOutput))
            return null;

        var provider = value.TryGetProperty("litellm_provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return new PriceEntry(key, provider, input.Value, output.Value, cacheRead, cacheWrite, maxInput, maxOutput);
    }

    /// <summary>欄位不存在：回 true 且 <paramref name="value"/> 是 null。存在且能讀：換算成每百萬 tokens 美元。存在但讀不懂（非數字、負數）：回 false。</summary>
    private static bool TryReadPerMillion(JsonElement obj, string name, out decimal? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var perToken) || perToken < 0)
            return false;

        value = decimal.Round(perToken * TokensPerMillion, PriceDecimals, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>同上，但數值是 token 數（不換算）。</summary>
    private static bool TryReadTokens(JsonElement obj, string name, out int? value)
    {
        value = null;
        if (!obj.TryGetProperty(name, out var element))
            return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var tokens) || tokens < 0)
            return false;

        value = tokens;
        return true;
    }
}
