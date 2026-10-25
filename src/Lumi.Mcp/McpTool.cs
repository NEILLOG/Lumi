using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Mcp;

/// <summary>
/// MCP 工具的 Adapter（M16 D3、D8、D10）：把伺服器的一個工具包成 <see cref="ITool"/>，和內建工具走同一條呼叫路徑。
/// <see cref="Descriptor"/> 每次讀取都重新計算，所以工具清單頁改了權限等級會立即生效。
/// </summary>
public sealed class McpTool : IMcpTool
{
    private const int MaxDescriptionLength = 2000;
    private const int MaxSummaryLength = 60;
    private const int MaxDetailLength = 1000;

    private readonly McpServerSettings _server;
    private readonly McpCachedTool _tool;
    private readonly IModelCatalog _catalog;
    private readonly IMcpToolCaller _caller;
    private readonly string _name;
    private JsonNode? _schema;

    /// <param name="server">建立時的伺服器設定（管理員每次取工具清單時都會傳最新的）。</param>
    /// <param name="isLive">true＝來自連線中的伺服器；false＝來自快取。</param>
    internal McpTool(McpServerSettings server, McpCachedTool tool, bool isLive, IModelCatalog catalog, IMcpToolCaller caller)
    {
        _server = server;
        _tool = tool;
        IsLive = isLive;
        _catalog = catalog;
        _caller = caller;
        _name = McpToolNaming.ToolName(server.Key, tool.Name);
    }

    public string ServerId => _server.Id;

    public bool IsLive { get; }

    public McpToolHints Hints => _tool.Hints;

    public ToolDescriptor Descriptor => new(
        Name: _name,
        DisplayName: string.IsNullOrWhiteSpace(_tool.Title) ? _tool.Name : _tool.Title,
        Description: Truncate($"（MCP 伺服器「{_server.Name}」）{_tool.Description}", MaxDescriptionLength),
        Permission: _catalog.Current.Tools.PermissionOverrides.TryGetValue(_name, out var permission) ? permission : ToolPermission.Write,
        Modes: _server.AlsoInSprite ? ToolModes.Both : ToolModes.Workbench,
        Timeout: TimeSpan.FromSeconds(_server.TimeoutSeconds),
        Group: "mcp:" + _server.Id,
        Summary: FirstSentence(_tool.Description));

    public JsonNode InputSchema => (_schema ??= BuildSchema(_tool.InputSchemaJson)).DeepClone();

    public ToolPreview Preview(string argumentsJson, ToolContext context) =>
        new(_server.Name, PrettyArguments(argumentsJson));

    public async Task<ToolResult> ExecuteAsync(string argumentsJson, ToolContext context, CancellationToken ct)
    {
        var json = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson;
        var shape = ArgumentShape(json);
        if (shape == ArgumentShapeKind.Invalid)
            return ToolResult.Error("參數不是有效的 JSON。", "失敗");
        if (shape == ArgumentShapeKind.NotObject)
            return ToolResult.Error("參數必須是 JSON 物件。", "失敗");

        try
        {
            var result = await _caller.CallAsync(_server.Id, _tool.Name, json, ct);
            return McpResultFormatter.Format(_server.Name, _tool.Name, result);
        }
        catch (McpCallException ex)
        {
            return ToolResult.Error(ex.Message, "失敗");
        }
    }

    /// <summary>說明的第一句（到第一個「。」「.」或換行），最多 60 字元；沒有時為 null。</summary>
    internal static string? FirstSentence(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var text = description.Trim();
        var end = text.IndexOfAny(['。', '.', '\n', '\r']);
        var sentence = (end >= 0 ? text[..end] : text).Trim();
        if (sentence.Length == 0)
            return null;
        return sentence.Length <= MaxSummaryLength ? sentence : sentence[..MaxSummaryLength];
    }

    /// <summary>伺服器給的 schema；頂層不是 object（或 type 不是 "object"）時包成空的 object schema。</summary>
    internal static JsonNode BuildSchema(string json)
    {
        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // 不是 JSON：下面用空的 schema
        }

        if (node is JsonObject obj && IsObjectType(obj["type"]))
        {
            obj["type"] ??= "object";
            obj["properties"] ??= new JsonObject();
            return obj;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(),
        };
    }

    private static bool IsObjectType(JsonNode? type) =>
        type is null || (type is JsonValue value && value.TryGetValue<string>(out var text) && text == "object");

    private enum ArgumentShapeKind { Object, NotObject, Invalid }

    private static ArgumentShapeKind ArgumentShape(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? ArgumentShapeKind.Object : ArgumentShapeKind.NotObject;
        }
        catch (JsonException)
        {
            return ArgumentShapeKind.Invalid;
        }
    }

    /// <summary>設定頁的「引數」：JSON 縮排後的前 1,000 字元；不是 JSON 時不顯示。</summary>
    private static string? PrettyArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return Truncate(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }), MaxDetailLength);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
