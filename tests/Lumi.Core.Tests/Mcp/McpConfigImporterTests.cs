using Lumi.Core.Mcp;

namespace Lumi.Core.Tests.Mcp;

public class McpConfigImporterTests
{
    private static McpServerSettings Existing(string name, string key) =>
        new(McpServerSettings.NewId(), key, name, McpTransportKind.Stdio, "npx", [], null, [], null, []);

    [Fact]
    public void ClaudeDesktopFormat_ImportsStdioServer()
    {
        const string json = """
        {
          "mcpServers": {
            "github": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-github"],
              "env": { "GITHUB_TOKEN": "ghp_secret" }
            }
          }
        }
        """;

        var preview = McpConfigImporter.Parse(json, []);

        Assert.Null(preview.Error);
        var item = Assert.Single(preview.Items);
        Assert.Null(item.Error);
        Assert.Null(item.RenamedFrom);
        Assert.Equal("github", item.Server.Name);
        Assert.Equal("github", item.Server.Key);
        Assert.Equal(McpTransportKind.Stdio, item.Server.Transport);
        Assert.Equal("npx", item.Server.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], item.Server.Args);
        Assert.Equal(["GITHUB_TOKEN"], item.Server.EnvNames);
        Assert.True(item.Server.Enabled);
        Assert.False(item.Server.AlsoInSprite);
        Assert.Equal(60, item.Server.TimeoutSeconds);
        Assert.Equal("ghp_secret", item.Secrets.Env["GITHUB_TOKEN"]);
        Assert.Empty(item.Secrets.Headers);
    }

    [Fact]
    public void VsCodeServersFormat_IsAccepted()
    {
        const string json = """{ "servers": { "kb": { "command": "uvx", "args": ["kb-mcp"] } } }""";

        var preview = McpConfigImporter.Parse(json, []);

        var item = Assert.Single(preview.Items);
        Assert.Null(item.Error);
        Assert.Equal("kb", item.Server.Name);
        Assert.Equal("uvx", item.Server.Command);
    }

    [Fact]
    public void DirectObjectFormat_IsAccepted()
    {
        const string json = """{ "notes": { "command": "node", "args": ["server.js"], "cwd": "/opt/notes" } }""";

        var preview = McpConfigImporter.Parse(json, []);

        var item = Assert.Single(preview.Items);
        Assert.Null(item.Error);
        Assert.Equal("/opt/notes", item.Server.WorkingDirectory);
    }

    [Fact]
    public void HttpServer_ImportsHeadersAsSecrets()
    {
        const string json = """
        {
          "mcpServers": {
            "内部": {
              "type": "http",
              "url": "https://kb.corp.test/mcp",
              "headers": { "Authorization": "Bearer abc" }
            }
          }
        }
        """;

        var preview = McpConfigImporter.Parse(json, []);

        var item = Assert.Single(preview.Items);
        Assert.Null(item.Error);
        Assert.Equal(McpTransportKind.Http, item.Server.Transport);
        Assert.Equal("https://kb.corp.test/mcp", item.Server.Url);
        Assert.Null(item.Server.Command);
        Assert.Equal(["Authorization"], item.Server.HeaderNames);
        Assert.Equal("Bearer abc", item.Secrets.Headers["Authorization"]);
        Assert.Empty(item.Secrets.Env);
    }

    [Theory]
    [InlineData("""{ "s": { "url": "https://a.test/mcp" } }""")]
    [InlineData("""{ "s": { "type": "streamable-http", "url": "https://a.test/mcp" } }""")]
    public void UrlWithoutTypeOrWithStreamableHttp_IsHttp(string json)
    {
        var item = Assert.Single(McpConfigImporter.Parse(json, []).Items);

        Assert.Null(item.Error);
        Assert.Equal(McpTransportKind.Http, item.Server.Transport);
    }

    [Fact]
    public void TypeSse_IsRejectedWithReason()
    {
        const string json = """{ "s": { "type": "sse", "url": "https://a.test/sse" } }""";

        var item = Assert.Single(McpConfigImporter.Parse(json, []).Items);

        Assert.Equal("不支援舊版 SSE 傳輸，請改用伺服器的 Streamable HTTP 網址", item.Error);
    }

    [Fact]
    public void MissingCommandAndUrl_IsRejected()
    {
        const string json = """{ "s": { "args": ["x"] } }""";

        var item = Assert.Single(McpConfigImporter.Parse(json, []).Items);

        Assert.Equal("缺少 command 或 url", item.Error);
    }

    [Fact]
    public void DuplicateNameOfExistingServer_IsRenamedWithSuffix()
    {
        const string json = """{ "mcpServers": { "GitHub": { "command": "npx" } } }""";
        var existing = new[] { Existing("GitHub", "github"), Existing("GitHub (2)", "github-2") };

        var item = Assert.Single(McpConfigImporter.Parse(json, existing).Items);

        Assert.Null(item.Error);
        Assert.Equal("GitHub (3)", item.Server.Name);
        Assert.Equal("GitHub", item.RenamedFrom);
    }

    [Fact]
    public void DuplicateNamesInSameBatch_AreRenamedAndKeysAreUnique()
    {
        const string json = """
        {
          "mcpServers": {
            "kb": { "command": "a" },
            "kb": { "command": "b" }
          }
        }
        """;
        // 同名的 JSON 物件鍵在 JsonDocument 裡會保留兩筆，兩筆都要處理。
        var items = McpConfigImporter.Parse(json, []).Items;

        Assert.Equal(2, items.Count);
        Assert.Equal("kb", items[0].Server.Name);
        Assert.Equal("kb (2)", items[1].Server.Name);
        Assert.Equal("kb", items[1].RenamedFrom);
        Assert.Equal("kb", items[0].Server.Key);
        Assert.Equal("kb-2", items[1].Server.Key);
    }

    [Fact]
    public void KeyFromName_AvoidsExistingKeys()
    {
        const string json = """{ "mcpServers": { "GitHub Tools": { "command": "npx" } } }""";
        var existing = new[] { Existing("Other", "github-tools") };

        var item = Assert.Single(McpConfigImporter.Parse(json, existing).Items);

        Assert.Equal("github-tools-2", item.Server.Key);
    }

    [Fact]
    public void InvalidJson_ReturnsError()
    {
        var preview = McpConfigImporter.Parse("{ not json", []);

        Assert.Empty(preview.Items);
        Assert.Equal("這不是有效的 JSON", preview.Error);
    }

    [Theory]
    [InlineData("""{ "other": 1 }""")]
    [InlineData("""{ "mcpServers": {} }""")]
    [InlineData("""[1, 2]""")]
    public void NoServers_ReturnsError(string json)
    {
        var preview = McpConfigImporter.Parse(json, []);

        Assert.Empty(preview.Items);
        Assert.Equal("找不到 mcpServers 或 servers", preview.Error);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAllowed()
    {
        const string json = """
        {
          // 這是註解
          "mcpServers": {
            "kb": { "command": "uvx", "args": ["kb",], }, /* 結尾逗號 */
          },
        }
        """;

        var preview = McpConfigImporter.Parse(json, []);

        Assert.Null(preview.Error);
        Assert.Null(Assert.Single(preview.Items).Error);
    }

    [Fact]
    public void EnvValueThatIsNotText_IsRejected()
    {
        const string json = """{ "mcpServers": { "s": { "command": "x", "env": { "PORT": 8080 } } } }""";

        var item = Assert.Single(McpConfigImporter.Parse(json, []).Items);

        Assert.Equal("環境變數 PORT 的值必須是文字", item.Error);
    }

    [Fact]
    public void HeaderValueThatIsNotText_IsRejected()
    {
        const string json = """{ "mcpServers": { "s": { "url": "https://a.test/mcp", "headers": { "X-N": 1 } } } }""";

        var item = Assert.Single(McpConfigImporter.Parse(json, []).Items);

        Assert.Equal("標頭 X-N 的值必須是文字", item.Error);
    }

    [Fact]
    public void ErroredItem_DoesNotReserveNameOrKey()
    {
        const string json = """
        {
          "mcpServers": {
            "kb": { "type": "sse", "url": "https://a.test/sse" },
            "kb": { "command": "uvx" }
          }
        }
        """;

        var items = McpConfigImporter.Parse(json, []).Items;

        Assert.NotNull(items[0].Error);
        Assert.Null(items[1].Error);
        Assert.Equal("kb", items[1].Server.Name);
        Assert.Null(items[1].RenamedFrom);
        Assert.Equal("kb", items[1].Server.Key);
    }

    [Fact]
    public void ValidationRules_ApplyToImportedServers()
    {
        const string json = """{ "mcpServers": { "s": { "url": "http://kb.corp.test/mcp" } } }""";

        var item = Assert.Single(McpConfigImporter.Parse(json, []).Items);

        Assert.Equal("遠端伺服器必須使用 https", item.Error);
    }
}
