using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M16 16B：settings.json 裡的 <c>mcpServers</c> 與 <c>tools.permissionOverrides</c>、祕密的讀寫與刪除。</summary>
public sealed class JsonModelCatalogMcpTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-mcp-settings-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();

    public JsonModelCatalogMcpTests() => Directory.CreateDirectory(_directory);

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), _secrets, NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static McpServerSettings Stdio(string id, string key) => new(
        id, key, "GitHub", McpTransportKind.Stdio, "npx", ["-y", "@modelcontextprotocol/server-github"], "/Users/someone/work", ["GITHUB_TOKEN"],
        null, [], Enabled: true, AlsoInSprite: true, TimeoutSeconds: 90);

    private static McpServerSettings Http(string id, string key) => new(
        id, key, "Docs", McpTransportKind.Http, null, [], null, [],
        "https://docs.example.com/mcp", ["Authorization"], Enabled: false);

    private static McpServerSecrets Secrets(IReadOnlyDictionary<string, string>? env = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new(env ?? new Dictionary<string, string>(), headers ?? new Dictionary<string, string>());

    [Fact]
    public async Task Stdio_and_http_servers_round_trip_through_file()
    {
        var catalog = NewCatalog();
        var stdio = Stdio("aaaa1111", "github");
        var http = Http("bbbb2222", "docs");
        await catalog.SaveMcpServerAsync(stdio, Secrets(env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_secret_1" }));
        await catalog.SaveMcpServerAsync(http, Secrets(headers: new Dictionary<string, string> { ["Authorization"] = "Bearer sk-http-2" }));

        var reloaded = NewCatalog();

        Assert.Null(reloaded.LoadProblem);
        Assert.Equal(["github", "docs"], reloaded.Current.McpServers.Select(s => s.Key));
        var s1 = reloaded.Current.McpServers[0];
        Assert.Equal(McpTransportKind.Stdio, s1.Transport);
        Assert.Equal("npx", s1.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-github"], s1.Args);
        Assert.Equal("/Users/someone/work", s1.WorkingDirectory);
        Assert.Equal(["GITHUB_TOKEN"], s1.EnvNames);
        Assert.True(s1.AlsoInSprite);
        Assert.Equal(90, s1.TimeoutSeconds);
        Assert.True(s1.Enabled);

        var s2 = reloaded.Current.McpServers[1];
        Assert.Equal(McpTransportKind.Http, s2.Transport);
        Assert.Equal("https://docs.example.com/mcp", s2.Url);
        Assert.Equal(["Authorization"], s2.HeaderNames);
        Assert.False(s2.Enabled);
        Assert.False(s2.AlsoInSprite);
        Assert.Equal(60, s2.TimeoutSeconds);

        var env = await reloaded.GetMcpSecretsAsync("aaaa1111");
        Assert.Equal("ghp_secret_1", env.Env["GITHUB_TOKEN"]);
        var headers = await reloaded.GetMcpSecretsAsync("bbbb2222");
        Assert.Equal("Bearer sk-http-2", headers.Headers["Authorization"]);
    }

    [Fact]
    public async Task Secret_values_are_never_written_to_settings_file()
    {
        await NewCatalog().SaveMcpServerAsync(
            Stdio("aaaa1111", "github"),
            Secrets(env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_secret_1" }));
        await NewCatalog().SaveMcpServerAsync(
            Http("bbbb2222", "docs"),
            Secrets(headers: new Dictionary<string, string> { ["Authorization"] = "Bearer sk-http-2" }));

        var json = File.ReadAllText(SettingsPath);

        Assert.DoesNotContain("ghp_secret_1", json);
        Assert.DoesNotContain("sk-http-2", json);
        Assert.Contains("\"GITHUB_TOKEN\"", json);   // 名稱留在設定檔
        Assert.Contains("mcp/aaaa1111/secrets", _secrets.Items.Keys);
        Assert.Contains("mcp/bbbb2222/secrets", _secrets.Items.Keys);
    }

    [Fact]
    public async Task Saving_without_new_secrets_keeps_secrets_and_version()
    {
        var catalog = NewCatalog();
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), Secrets(env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_1" }));
        var version = NewCatalog().Current.McpServers.Single().SecretsVersion;

        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github") with { Name = "GitHub 2", SecretsVersion = "forged" }, null);

        var reloaded = NewCatalog();
        var server = reloaded.Current.McpServers.Single();
        Assert.Equal("GitHub 2", server.Name);
        Assert.Equal(version, server.SecretsVersion);
        Assert.Equal(["GITHUB_TOKEN"], server.EnvNames);
        Assert.Equal("ghp_1", (await reloaded.GetMcpSecretsAsync("aaaa1111")).Env["GITHUB_TOKEN"]);
    }

    [Fact]
    public async Task Writing_secrets_changes_secrets_version()
    {
        var catalog = NewCatalog();
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), Secrets(env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_1" }));
        var first = NewCatalog().Current.McpServers.Single().SecretsVersion;

        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), Secrets(env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_2" }));

        var reloaded = NewCatalog();
        Assert.NotNull(first);
        Assert.NotEqual(first, reloaded.Current.McpServers.Single().SecretsVersion);
        Assert.Equal("ghp_2", (await reloaded.GetMcpSecretsAsync("aaaa1111")).Env["GITHUB_TOKEN"]);
    }

    [Fact]
    public async Task Changing_secrets_to_new_names_updates_saved_names()
    {
        var catalog = NewCatalog();
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), Secrets(env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_1" }));

        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github") with { EnvNames = [] },
            Secrets(env: new Dictionary<string, string> { ["OTHER_TOKEN"] = "x" }));

        var server = NewCatalog().Current.McpServers.Single();
        Assert.Equal(["OTHER_TOKEN"], server.EnvNames);
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("a_b")]
    [InlineData("-github")]
    [InlineData("")]
    [InlineData("abcdefghijklmnopq")]   // 17 個字
    public async Task Invalid_key_is_rejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewCatalog().SaveMcpServerAsync(Stdio("aaaa1111", key), null));

        Assert.Empty(NewCatalog().Current.McpServers);
    }

    [Fact]
    public async Task Duplicate_key_of_another_server_is_rejected_but_updating_the_same_server_is_allowed()
    {
        var catalog = NewCatalog();
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), null);

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SaveMcpServerAsync(Stdio("bbbb2222", "github"), null));
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github") with { Name = "改名" }, null);

        var servers = NewCatalog().Current.McpServers;
        Assert.Single(servers);
        Assert.Equal("改名", servers[0].Name);
    }

    [Fact]
    public async Task Delete_removes_server_secrets_and_its_tool_settings_only()
    {
        var catalog = NewCatalog();
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), Secrets(env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_1" }));
        await catalog.SaveMcpServerAsync(Http("bbbb2222", "docs"), Secrets(headers: new Dictionary<string, string> { ["Authorization"] = "Bearer x" }));
        await catalog.SetToolsAsync(new ToolSettings
        {
            Disabled = ["mcp__github__get_issue", "copy_file"],
            AlwaysAllow = ["mcp__github__create_issue", "mcp__docs__search", "add_note"],
            PermissionOverrides = new Dictionary<string, ToolPermission>
            {
                ["mcp__github__get_issue"] = ToolPermission.ReadOnly,
                ["mcp__docs__search"] = ToolPermission.Network,
            },
        });

        await catalog.DeleteMcpServerAsync("aaaa1111");

        var reloaded = NewCatalog();
        Assert.Equal(["bbbb2222"], reloaded.Current.McpServers.Select(s => s.Id));
        Assert.Equal(["copy_file"], reloaded.Current.Tools.Disabled);
        Assert.Equal(["add_note", "mcp__docs__search"], reloaded.Current.Tools.AlwaysAllow);
        Assert.Equal(["mcp__docs__search"], reloaded.Current.Tools.PermissionOverrides.Keys);
        Assert.DoesNotContain("mcp/aaaa1111/secrets", _secrets.Items.Keys);
        Assert.Contains("mcp/bbbb2222/secrets", _secrets.Items.Keys);
    }

    [Fact]
    public async Task Deleting_unknown_server_does_nothing_harmful()
    {
        var catalog = NewCatalog();
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), null);

        await catalog.DeleteMcpServerAsync("ffff9999");

        Assert.Single(NewCatalog().Current.McpServers);
    }

    [Fact]
    public void Broken_entries_are_skipped_one_by_one()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "mcpServers": [
                { "id": "aaaa1111", "key": "github", "name": "GitHub", "transport": "stdio", "command": "npx", "args": ["-y", "x"] },
                { "id": "bad1", "key": "x", "name": "壞掉", "transport": "carrier-pigeon", "command": "npx" },
                42,
                { "id": "bad2", "key": "BAD KEY", "name": "代號不合法", "transport": "stdio", "command": "npx" },
                { "id": "dup1", "key": "github", "name": "重複代號", "transport": "stdio", "command": "npx" },
                { "id": "cccc3333", "key": "notion", "name": "Notion", "transport": "http", "url": "https://notion.example.com/mcp" }
              ]
            }
            """);

        var catalog = NewCatalog();

        Assert.Null(catalog.LoadProblem);
        Assert.Equal(["aaaa1111", "cccc3333"], catalog.Current.McpServers.Select(s => s.Id));
        Assert.Equal(["github", "notion"], catalog.Current.McpServers.Select(s => s.Key));
        Assert.Empty(Directory.GetFiles(_directory, "settings.json.broken-*"));
    }

    [Fact]
    public void Old_settings_without_mcp_servers_load_as_empty()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "dark"
            }
            """);

        var catalog = NewCatalog();

        Assert.Empty(catalog.Current.McpServers);
        Assert.Null(catalog.LoadProblem);
    }

    [Theory]
    [InlineData("\"oops\"")]
    [InlineData("{\"id\": \"aaaa1111\"}")]
    [InlineData("42")]
    public void Malformed_mcp_servers_section_is_empty_and_keeps_the_rest(string value)
    {
        File.WriteAllText(SettingsPath, $$"""
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "light",
              "mcpServers": {{value}}
            }
            """);

        var catalog = NewCatalog();

        Assert.Empty(catalog.Current.McpServers);
        Assert.Equal(ThemePreference.Light, catalog.Current.Theme);
        Assert.Null(catalog.LoadProblem);
        Assert.Empty(Directory.GetFiles(_directory, "settings.json.broken-*"));
    }

    [Fact]
    public void Malformed_permission_override_value_uses_default_tools_section()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "tools": { "disabled": ["copy_file"], "permissionOverrides": { "mcp__x__y": "Everything" } }
            }
            """);

        var catalog = NewCatalog();

        Assert.Empty(catalog.Current.Tools.Disabled);
        Assert.Empty(catalog.Current.Tools.PermissionOverrides);
        Assert.Null(catalog.LoadProblem);
    }

    [Fact]
    public async Task Permission_overrides_round_trip_through_file()
    {
        await NewCatalog().SetToolsAsync(new ToolSettings
        {
            PermissionOverrides = new Dictionary<string, ToolPermission> { ["mcp__github__get_issue"] = ToolPermission.ReadOnly },
        });

        var reloaded = NewCatalog();

        Assert.Equal(ToolPermission.ReadOnly, reloaded.Current.Tools.PermissionOverrides["mcp__github__get_issue"]);
        Assert.Contains("\"permissionOverrides\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task Secret_with_broken_json_reads_as_empty_without_throwing()
    {
        var catalog = NewCatalog();
        await catalog.SaveMcpServerAsync(Stdio("aaaa1111", "github"), null);
        _secrets.Items["mcp/aaaa1111/secrets"] = "{ 不是 JSON";

        var secrets = await NewCatalog().GetMcpSecretsAsync("aaaa1111");

        Assert.Empty(secrets.Env);
        Assert.Empty(secrets.Headers);
    }

    [Fact]
    public async Task Missing_secret_reads_as_empty()
    {
        var secrets = await NewCatalog().GetMcpSecretsAsync("aaaa1111");

        Assert.Same(McpServerSecrets.Empty, secrets);
    }
}
