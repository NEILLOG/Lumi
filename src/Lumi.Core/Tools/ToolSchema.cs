using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

namespace Lumi.Core.Tools;

/// <summary>
/// 工具引數的序列化設定與 JSON Schema 產生（agent.md 第 8 節：Schema 由 <c>JsonSchemaExporter</c> 自動產生，不手寫）。
/// 屬性名稱用 snake_case（模型最常用的工具參數寫法）；引數是強型別 record，欄位說明用 <c>[Description]</c>。
/// </summary>
public static class ToolSchema
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static JsonNode Generate(Type argsType)
    {
        var node = JsonSchemaExporter.GetJsonSchemaAsNode(Options, argsType, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        });

        var type = (node as JsonObject)?["type"]?.GetValue<string>();
        if (type != "object")
            throw new InvalidOperationException($"工具引數型別 {argsType.Name} 必須是物件（record 或 class），不能是 {type ?? "其他"}。");

        return node;
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject obj)
            return schema;

        // 欄位說明：[Description] 可以寫在屬性上，或 record 建構子的參數上
        var description = DescriptionOf(context.PropertyInfo?.AttributeProvider)
                          ?? DescriptionOf(context.PropertyInfo?.AssociatedParameter?.AttributeProvider)
                          ?? (context.PropertyInfo is null ? DescriptionOf(context.TypeInfo.Type) : null);
        if (description is not null && !obj.ContainsKey("description"))
            obj["description"] = description;

        // 兩家 API 都偏好（或要求）不允許多餘的欄位
        if (obj["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type == "object" && !obj.ContainsKey("additionalProperties"))
            obj["additionalProperties"] = false;

        return obj;
    }

    private static string? DescriptionOf(ICustomAttributeProvider? provider) =>
        provider?.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true) is [DescriptionAttribute attribute, ..]
            ? attribute.Description
            : null;

    private static string? DescriptionOf(Type type) => type.GetCustomAttribute<DescriptionAttribute>()?.Description;
}
