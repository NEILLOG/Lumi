using System.Text.Json;

namespace Lumi.Core.Mcp;

/// <summary>匯入預覽的一筆：有 <see cref="Error"/> 的那筆不會寫入。</summary>
/// <param name="RenamedFrom">因為名稱重複而改名時，原來的名稱。</param>
public sealed record McpImportItem(McpServerSettings Server, McpServerSecrets Secrets, string? RenamedFrom, string? Error);

/// <param name="Error">整份 JSON 無法解析（或找不到伺服器）時的原因。</param>
public sealed record McpImportPreview(IReadOnlyList<McpImportItem> Items, string? Error);

/// <summary>
/// 解析貼上的 MCP 伺服器 JSON（Claude Desktop、Claude Code、VS Code 的格式，M16 D5）。只做解析與驗證，不寫入任何地方。
/// </summary>
public static class McpConfigImporter
{
    private const string NoServersError = "找不到 mcpServers 或 servers";

    public static McpImportPreview Parse(string json, IReadOnlyList<McpServerSettings> existing)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException)
        {
            return new McpImportPreview([], "這不是有效的 JSON");
        }

        using (document)
        {
            var entries = FindEntries(document.RootElement);
            if (entries is null || entries.Count == 0)
                return new McpImportPreview([], NoServersError);

            // 已存在的名稱與代號，加上同一批已接受的項目。有錯誤的項目不佔用名稱與代號。
            var takenNames = new HashSet<string>(existing.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            var takenKeys = existing.Select(s => s.Key).ToList();
            var items = new List<McpImportItem>();

            foreach (var (rawName, value) in entries)
            {
                var baseName = rawName.Trim();
                var name = UniqueName(baseName, takenNames);
                var renamedFrom = name != baseName ? baseName : null;
                var key = McpToolNaming.KeyFromName(name, takenKeys);
                var item = BuildItem(McpServerSettings.NewId(), key, name, renamedFrom, value);
                items.Add(item);

                if (item.Error is null)
                {
                    takenNames.Add(name);
                    takenKeys.Add(key);
                }
            }

            return new McpImportPreview(items, null);
        }
    }

    /// <summary>mcpServers、servers，或直接是 {名稱: 設定}（每個值都必須是物件）。</summary>
    private static List<(string Name, JsonElement Value)>? FindEntries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("mcpServers", out var mcpServers))
            return mcpServers.ValueKind == JsonValueKind.Object ? Properties(mcpServers) : null;

        if (root.TryGetProperty("servers", out var servers))
            return servers.ValueKind == JsonValueKind.Object ? Properties(servers) : null;

        var direct = Properties(root);
        return direct.Count > 0 && direct.All(e => e.Value.ValueKind == JsonValueKind.Object) ? direct : null;
    }

    private static List<(string Name, JsonElement Value)> Properties(JsonElement obj)
    {
        return obj.EnumerateObject().Select(p => (p.Name, p.Value)).ToList();
    }

    /// <summary>名稱重複時依序接上 " (2)"、" (3)"…。空白名稱不改名（交給 <see cref="McpServerRules.ValidateName"/>）。</summary>
    private static string UniqueName(string name, HashSet<string> taken)
    {
        if (name.Length == 0 || !taken.Contains(name))
            return name;

        for (var n = 2; ; n++)
        {
            var candidate = $"{name} ({n})";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private static McpImportItem BuildItem(string id, string key, string name, string? renamedFrom, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return Fail(id, key, name, renamedFrom, "缺少 command 或 url");

        if (McpServerRules.ValidateName(name) is { } nameError)
            return Fail(id, key, name, renamedFrom, nameError);

        var typeElement = Property(value, "type");
        if (typeElement is { ValueKind: not JsonValueKind.String })
            return Fail(id, key, name, renamedFrom, "欄位 type 的格式不正確");
        var type = typeElement?.GetString() ?? "";

        if (type == "sse")
            return Fail(id, key, name, renamedFrom, "不支援舊版 SSE 傳輸，請改用伺服器的 Streamable HTTP 網址");

        var command = Property(value, "command");
        if (command is not null)
            return BuildStdio(id, key, name, renamedFrom, value, command.Value);

        var url = Property(value, "url");
        if (url is not null)
        {
            if (type is not ("" or "http" or "streamable-http"))
                return Fail(id, key, name, renamedFrom, $"不支援的傳輸類型「{type}」");
            return BuildHttp(id, key, name, renamedFrom, value, url.Value);
        }

        return Fail(id, key, name, renamedFrom, "缺少 command 或 url");
    }

    private static McpImportItem BuildStdio(string id, string key, string name, string? renamedFrom, JsonElement value, JsonElement command)
    {
        if (command.ValueKind != JsonValueKind.String)
            return Fail(id, key, name, renamedFrom, "欄位 command 的格式不正確");
        var commandText = command.GetString()!;
        if (McpServerRules.ValidateCommand(commandText) is { } commandError)
            return Fail(id, key, name, renamedFrom, commandError);

        var args = new List<string>();
        if (Property(value, "args") is { } argsElement)
        {
            if (argsElement.ValueKind != JsonValueKind.Array)
                return Fail(id, key, name, renamedFrom, "欄位 args 的格式不正確");
            foreach (var arg in argsElement.EnumerateArray())
            {
                if (arg.ValueKind != JsonValueKind.String)
                    return Fail(id, key, name, renamedFrom, "欄位 args 的格式不正確");
                args.Add(arg.GetString()!);
            }
        }

        string? workingDirectory = null;
        if (Property(value, "cwd") is { } cwdElement)
        {
            if (cwdElement.ValueKind != JsonValueKind.String)
                return Fail(id, key, name, renamedFrom, "欄位 cwd 的格式不正確");
            workingDirectory = cwdElement.GetString();
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Property(value, "env") is { } envElement)
        {
            if (envElement.ValueKind != JsonValueKind.Object)
                return Fail(id, key, name, renamedFrom, "欄位 env 的格式不正確");
            foreach (var (envName, envValue) in Properties(envElement))
            {
                if (McpServerRules.ValidateEnvName(envName) is { } envNameError)
                    return Fail(id, key, name, renamedFrom, envNameError);
                if (envValue.ValueKind != JsonValueKind.String)
                    return Fail(id, key, name, renamedFrom, $"環境變數 {envName} 的值必須是文字");
                env[envName] = envValue.GetString()!;
            }
        }

        var server = new McpServerSettings(
            id, key, name, McpTransportKind.Stdio, commandText, args, workingDirectory,
            env.Keys.ToList(), null, []);
        return new McpImportItem(server, new McpServerSecrets(env, new Dictionary<string, string>()), renamedFrom, null);
    }

    private static McpImportItem BuildHttp(string id, string key, string name, string? renamedFrom, JsonElement value, JsonElement url)
    {
        if (url.ValueKind != JsonValueKind.String)
            return Fail(id, key, name, renamedFrom, "欄位 url 的格式不正確");
        var urlText = url.GetString()!;
        if (McpServerRules.ValidateUrl(urlText) is { } urlError)
            return Fail(id, key, name, renamedFrom, urlError);

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Property(value, "headers") is { } headersElement)
        {
            if (headersElement.ValueKind != JsonValueKind.Object)
                return Fail(id, key, name, renamedFrom, "欄位 headers 的格式不正確");
            foreach (var (headerName, headerValue) in Properties(headersElement))
            {
                if (McpServerRules.ValidateHeaderName(headerName) is { } headerNameError)
                    return Fail(id, key, name, renamedFrom, headerNameError);
                if (headerValue.ValueKind != JsonValueKind.String)
                    return Fail(id, key, name, renamedFrom, $"標頭 {headerName} 的值必須是文字");
                headers[headerName] = headerValue.GetString()!;
            }
        }

        var server = new McpServerSettings(
            id, key, name, McpTransportKind.Http, null, [], null,
            [], urlText, headers.Keys.ToList());
        return new McpImportItem(server, new McpServerSecrets(new Dictionary<string, string>(), headers), renamedFrom, null);
    }

    /// <summary>有錯誤的項目仍要有一個伺服器物件（預覽用），但不會寫入。</summary>
    private static McpImportItem Fail(string id, string key, string name, string? renamedFrom, string error)
    {
        var server = new McpServerSettings(id, key, name, McpTransportKind.Stdio, null, [], null, [], null, []);
        return new McpImportItem(server, McpServerSecrets.Empty, renamedFrom, error);
    }

    /// <summary>屬性存在且不是 null 才回傳。</summary>
    private static JsonElement? Property(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var element) && element.ValueKind != JsonValueKind.Null)
            return element;
        return null;
    }
}
