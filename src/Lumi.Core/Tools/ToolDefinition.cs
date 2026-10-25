using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumi.Core.Tools;

/// <summary>
/// 送給供應商的工具定義（名稱、說明、input schema）。對話第一次送出時存成快照，之後原樣送出（M15 D1、D2）：
/// Anthropic 的 thinking 簽章綁定工具清單，中途改變會 400。
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonNode InputSchema)
{
    public static ToolDefinition From(ITool tool) =>
        new(tool.Descriptor.Name, tool.Descriptor.Description, tool.InputSchema.DeepClone());
}

/// <summary>工具快照的 JSON 格式：<c>[{"name","description","input_schema"}]</c>。</summary>
public static class ToolSnapshotJson
{
    public static string Serialize(IReadOnlyList<ToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var t in tools)
        {
            array.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone(),
            });
        }
        return array.ToJsonString();
    }

    /// <summary>null、空字串、格式錯誤一律回 null（呼叫端當成「還沒有快照」）。</summary>
    public static IReadOnlyList<ToolDefinition>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray array)
                return null;
            var result = new List<ToolDefinition>(array.Count);
            foreach (var item in array)
            {
                if (item is not JsonObject o
                    || o["name"]?.GetValue<string>() is not { Length: > 0 } name
                    || o["description"]?.GetValue<string>() is not { } description
                    || o["input_schema"] is not JsonNode schema)
                    return null;
                result.Add(new ToolDefinition(name, description, schema.DeepClone()));
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
