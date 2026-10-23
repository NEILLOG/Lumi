using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumi.Tools.Reminders;

/// <summary>
/// 字串陣列的寬鬆讀取：元素可以是字串（<c>"5"</c>、<c>"monday"</c>）或整數（<c>5</c>），一律轉成字串。
/// 只影響解析；JSON Schema 仍由 <see cref="Lumi.Core.Tools.ToolSchema"/> 依型別產生（字串陣列）。
/// 模型常把數字星期寫成 JSON 數字，這裡兼容，不因此失敗。
/// </summary>
public sealed class LooseStringListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("days 必須是陣列。");

        var items = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    items.Add(reader.GetString() ?? "");
                    break;
                case JsonTokenType.Number when reader.TryGetInt64(out var number):
                    items.Add(number.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new JsonException("days 的元素必須是字串或整數。");
            }
        }
        return items;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
