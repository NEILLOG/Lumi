using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumi.Providers;

/// <summary>兩家 Provider 共用的工具 JSON 小工具。</summary>
internal static class ToolJson
{
    /// <summary>
    /// 模型給的引數 JSON（字串）轉成物件的欄位表。空字串、不是物件、解析失敗一律當成沒有引數：
    /// 這份是「送回給模型的歷史」，模型自己產生的壞 JSON 已經在執行工具時以錯誤結果告訴它了。
    /// </summary>
    public static Dictionary<string, JsonElement> ParseObject(string json)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                    result[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException)
        {
        }
        return result;
    }

    /// <summary>是不是完整、合法的 JSON 物件（空字串不算）。</summary>
    public static bool IsJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static Dictionary<string, JsonElement> SchemaFields(JsonNode schema)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in schema.AsObject())
            fields[key] = JsonSerializer.SerializeToElement(value);
        return fields;
    }
}
