using System.Text.RegularExpressions;
using Lumi.Core.Mcp;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M16 16B：MCP 工具快取（<c>mcp_tool_cache</c>）的讀寫、覆寫、刪除與重新開啟後仍在。</summary>
public sealed class SqliteMcpToolCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-mcp-cache-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteMcpToolCache NewCache() => new(
        new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath })),
        TimeProvider.System,
        NullLogger<SqliteMcpToolCache>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static McpCachedTool Tool(string name, string description = "說明", bool? readOnly = null) =>
        new(name, null, description, """{"type":"object","properties":{"q":{"type":"string"}}}""", new McpToolHints(readOnly, null));

    [Fact]
    public async Task Nothing_saved_returns_empty()
    {
        Assert.Empty(await NewCache().GetAsync("aaaa1111", CancellationToken.None));
    }

    [Fact]
    public async Task Saved_tools_round_trip_with_hints_and_schema()
    {
        var cache = NewCache();
        var tools = new[]
        {
            new McpCachedTool("get_issue", "取得議題", "讀取一個議題", """{"type":"object"}""", new McpToolHints(true, false)),
            Tool("create_issue"),
        };

        await cache.SaveAsync("aaaa1111", tools, CancellationToken.None);

        var loaded = await cache.GetAsync("aaaa1111", CancellationToken.None);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("get_issue", loaded[0].Name);
        Assert.Equal("取得議題", loaded[0].Title);
        Assert.Equal("讀取一個議題", loaded[0].Description);
        Assert.Equal("""{"type":"object"}""", loaded[0].InputSchemaJson);
        Assert.Equal(new McpToolHints(true, false), loaded[0].Hints);
        Assert.Null(loaded[1].Title);
        Assert.Equal(new McpToolHints(null, null), loaded[1].Hints);
    }

    [Fact]
    public async Task Save_replaces_previous_list_and_leaves_other_servers_alone()
    {
        var cache = NewCache();
        await cache.SaveAsync("aaaa1111", [Tool("old_tool"), Tool("another")], CancellationToken.None);
        await cache.SaveAsync("bbbb2222", [Tool("keep_me")], CancellationToken.None);

        await cache.SaveAsync("aaaa1111", [Tool("new_tool")], CancellationToken.None);

        var loaded = await cache.GetAsync("aaaa1111", CancellationToken.None);
        Assert.Equal(["new_tool"], loaded.Select(t => t.Name));
        Assert.Equal(["keep_me"], (await cache.GetAsync("bbbb2222", CancellationToken.None)).Select(t => t.Name));
    }

    [Fact]
    public async Task Saving_empty_list_is_kept_as_empty_not_missing()
    {
        var cache = NewCache();
        await cache.SaveAsync("aaaa1111", [Tool("gone")], CancellationToken.None);

        await cache.SaveAsync("aaaa1111", [], CancellationToken.None);

        Assert.Empty(await cache.GetAsync("aaaa1111", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_entry_and_missing_delete_is_fine()
    {
        var cache = NewCache();
        await cache.SaveAsync("aaaa1111", [Tool("x")], CancellationToken.None);

        await cache.DeleteAsync("aaaa1111", CancellationToken.None);
        await cache.DeleteAsync("aaaa1111", CancellationToken.None);

        Assert.Empty(await cache.GetAsync("aaaa1111", CancellationToken.None));
    }

    [Fact]
    public async Task Cache_survives_reopening_the_database()
    {
        await NewCache().SaveAsync("aaaa1111", [Tool("persisted")], CancellationToken.None);
        SqliteConnection.ClearAllPools();

        var loaded = await NewCache().GetAsync("aaaa1111", CancellationToken.None);

        Assert.Equal(["persisted"], loaded.Select(t => t.Name));
    }

    [Fact]
    public async Task Broken_tools_json_reads_as_empty()
    {
        var cache = NewCache();
        await cache.SaveAsync("aaaa1111", [Tool("x")], CancellationToken.None);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mcp_tool_cache SET tools_json = '[not json';";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        Assert.Empty(await NewCache().GetAsync("aaaa1111", CancellationToken.None));
    }

    [Fact]
    public async Task Updated_at_is_stored_as_utc_iso_8601()
    {
        await NewCache().SaveAsync("aaaa1111", [Tool("x")], CancellationToken.None);
        SqliteConnection.ClearAllPools();

        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT updated_at FROM mcp_tool_cache WHERE server_id = 'aaaa1111';";
        var updatedAt = (string?)command.ExecuteScalar();

        Assert.NotNull(updatedAt);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$"), updatedAt);
    }
}
