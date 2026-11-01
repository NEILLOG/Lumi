using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Lumi.Tarot;

/// <summary>
/// 把 tarot_draw 的結果 JSON（<c>DrawResultView</c>，snake_case）解析成畫面用的牌陣。
/// 工具卡片靠它判斷「這是牌陣結果，要畫牌陣圖」，所以任何格式問題都只回 false，不丟例外。
/// </summary>
public static class TarotSpreadParser
{
    /// <summary>解析 tarot_draw 的結果 JSON；格式不對回 false（不丟例外）。</summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out TarotSpreadModel? model)
    {
        model = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var spreadName = GetString(root, "spread_name");
            var allowReversed = GetBool(root, "allow_reversed");
            var positions = root.GetProperty("positions");
            if (positions.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var cards = positions.EnumerateArray().Select(ParsePosition).OrderBy(c => c.Index).ToList();
            if (cards.Count == 0)
            {
                return false;
            }

            model = new TarotSpreadModel(spreadName, allowReversed, cards);
            return true;
        }
        catch (Exception)
        {
            model = null;
            return false;
        }
    }

    private static TarotPlacedCard ParsePosition(JsonElement position)
    {
        var card = position.GetProperty("card");
        var layout = position.GetProperty("layout");

        return new TarotPlacedCard(
            Index: GetInt(position, "index"),
            PositionName: GetString(position, "position_name"),
            CardId: GetString(card, "id"),
            CardNameZh: GetString(card, "name_zh"),
            CardNameEn: GetString(card, "name_en"),
            Element: GetOptionalString(card, "element"),
            IsReversed: GetString(card, "orientation") == "reversed",
            X: GetDouble(layout, "x"),
            Y: GetDouble(layout, "y"),
            Rotation: GetDouble(layout, "rotation"),
            Z: GetInt(layout, "z"));
    }

    // 以下的讀取方式：欄位缺少或型別不對就丟 FormatException（或 KeyNotFoundException），由 TryParse 統一接住

    private static string GetString(JsonElement obj, string name)
    {
        var value = obj.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"欄位 {name} 不是字串。");
    }

    /// <summary>可以是 null 或缺少（例如大牌沒有元素）。</summary>
    private static string? GetOptionalString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new FormatException($"欄位 {name} 不是字串。");
    }

    private static bool GetBool(JsonElement obj, string name)
    {
        var value = obj.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"欄位 {name} 不是布林值。"),
        };
    }

    private static int GetInt(JsonElement obj, string name)
    {
        var value = obj.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new FormatException($"欄位 {name} 不是整數。");
    }

    private static double GetDouble(JsonElement obj, string name)
    {
        var value = obj.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)
            ? result
            : throw new FormatException($"欄位 {name} 不是數字。");
    }
}
