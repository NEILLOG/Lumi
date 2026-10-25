using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Lumi.Mcp.Tests.TestSupport;

namespace Lumi.Mcp.Tests;

public sealed class McpToolTests
{
    private static (McpTool Tool, FakeMcpModelCatalog Catalog, FakeMcpToolCaller Caller) Create(
        McpServerSettings server, McpCachedTool tool, bool isLive = true)
    {
        var catalog = new FakeMcpModelCatalog(server);
        var caller = new FakeMcpToolCaller();
        return (new McpTool(server, tool, isLive, catalog, caller), catalog, caller);
    }

    [Fact]
    public void Descriptor_maps_the_server_and_tool_fields()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(key: "github", name: "GitHub"),
            McpFixtures.Tool("get_issue", description: "取得 issue。包含留言。", title: "取得 Issue"));

        var d = tool.Descriptor;

        Assert.Equal("mcp__github__get_issue", d.Name);
        Assert.Equal("取得 Issue", d.DisplayName);
        Assert.Equal("（MCP 伺服器「GitHub」）取得 issue。包含留言。", d.Description);
        Assert.Equal(ToolPermission.Write, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal("mcp:s1", d.Group);
        Assert.Equal("取得 issue", d.Summary);
        Assert.Equal("s1", tool.ServerId);
    }

    [Fact]
    public void Display_name_falls_back_to_the_tool_name_without_a_title()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search", title: " "));

        Assert.Equal("search", tool.Descriptor.DisplayName);
    }

    [Fact]
    public void Sprite_mode_is_available_only_when_the_server_allows_it()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(alsoInSprite: true), McpFixtures.Tool("search"));

        Assert.Equal(ToolModes.Both, tool.Descriptor.Modes);
    }

    [Fact]
    public void Permission_override_is_read_each_time_the_descriptor_is_read()
    {
        var (tool, catalog, _) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));
        Assert.Equal(ToolPermission.Write, tool.Descriptor.Permission);

        catalog.SetTools(new ToolSettings
        {
            PermissionOverrides = new Dictionary<string, ToolPermission> { ["mcp__github__search"] = ToolPermission.ReadOnly },
        });

        Assert.Equal(ToolPermission.ReadOnly, tool.Descriptor.Permission);
    }

    [Fact]
    public void Long_names_are_shortened_with_a_hash()
    {
        var longName = new string('a', 70);
        var (tool, _, _) = Create(McpFixtures.Stdio(key: "github"), McpFixtures.Tool(longName));

        var name = tool.Descriptor.Name;

        Assert.Equal(McpToolNaming.ToolName("github", longName), name);
        Assert.Equal(McpToolNaming.MaxLength, name.Length);
    }

    [Fact]
    public void Summary_is_the_first_sentence_limited_to_60_characters()
    {
        Assert.Equal("第一句", McpTool.FirstSentence("第一句。第二句。"));
        Assert.Equal("first", McpTool.FirstSentence("first. second"));
        Assert.Equal("line one", McpTool.FirstSentence("line one\nline two"));
        Assert.Null(McpTool.FirstSentence("   "));
        Assert.Equal(new string('x', 60), McpTool.FirstSentence(new string('x', 100)));
    }

    [Fact]
    public void Hints_are_passed_through_as_suggestions()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search", hints: new McpToolHints(true, false)));

        Assert.True(tool.Hints.ReadOnly);
        Assert.False(tool.Hints.Destructive);
    }

    [Fact]
    public void Non_object_schema_is_replaced_by_an_empty_object_schema()
    {
        var node = McpTool.BuildSchema("[1,2]");

        Assert.Equal("object", node["type"]!.GetValue<string>());
        Assert.Empty(node["properties"]!.AsObject());
    }

    [Fact]
    public void Object_schema_is_kept_and_gets_missing_properties()
    {
        var node = McpTool.BuildSchema("{\"type\":\"object\",\"required\":[\"q\"]}");

        Assert.Equal("object", node["type"]!.GetValue<string>());
        Assert.NotNull(node["properties"]);
        Assert.Equal("q", node["required"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void Schema_with_another_type_is_replaced()
    {
        var node = McpTool.BuildSchema("{\"type\":\"string\"}");

        Assert.Equal("object", node["type"]!.GetValue<string>());
        Assert.Null(node["required"]);
    }

    [Fact]
    public void Input_schema_is_a_copy_each_time()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));

        tool.InputSchema["type"] = "changed";

        Assert.Equal("object", tool.InputSchema["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bad_json_arguments_are_rejected_without_calling_the_server()
    {
        var (tool, _, caller) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));

        var result = await tool.ExecuteAsync("{not json", McpFixtures.Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("參數不是有效的 JSON。", result.Content);
        Assert.Empty(caller.Calls);
    }

    [Fact]
    public async Task Non_object_arguments_are_rejected()
    {
        var (tool, _, caller) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));

        var result = await tool.ExecuteAsync("[1]", McpFixtures.Context(), default);

        Assert.True(result.IsError);
        Assert.Empty(caller.Calls);
    }

    [Fact]
    public async Task Successful_call_is_wrapped_and_summarized_as_done()
    {
        var (tool, _, caller) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));
        caller.Result = () => new McpCallResult(false, [new McpTextPart("found 3")]);

        var result = await tool.ExecuteAsync("{\"q\":\"x\"}", McpFixtures.Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("完成", result.Summary);
        Assert.Contains("found 3", result.Content);
        Assert.Equal(("s1", "search", "{\"q\":\"x\"}"), caller.Calls.Single());
    }

    [Fact]
    public async Task Connection_failure_is_returned_as_a_tool_error()
    {
        var (tool, _, caller) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));
        caller.Throw = new McpCallException("MCP 伺服器「GitHub」目前沒有連線。");

        var result = await tool.ExecuteAsync("{}", McpFixtures.Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("失敗", result.Summary);
        Assert.Equal("MCP 伺服器「GitHub」目前沒有連線。", result.Content);
    }

    [Fact]
    public void Preview_names_the_server_and_pretty_prints_the_arguments()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(name: "GitHub"), McpFixtures.Tool("search"));

        var preview = tool.Preview("{\"q\":\"x\"}", McpFixtures.Context());

        Assert.Equal("GitHub", preview.Target);
        Assert.Contains("\n", preview.Detail);
        Assert.Contains("\"q\": \"x\"", preview.Detail);
    }

    [Fact]
    public void Preview_detail_is_limited_to_1000_characters()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));
        var json = JsonSerializer.Serialize(new { text = new string('a', 2000) });

        var preview = tool.Preview(json, McpFixtures.Context());

        Assert.Equal(1000, preview.Detail!.Length);
    }

    [Fact]
    public void Preview_has_no_detail_for_invalid_json()
    {
        var (tool, _, _) = Create(McpFixtures.Stdio(), McpFixtures.Tool("search"));

        Assert.Null(tool.Preview("{oops", McpFixtures.Context()).Detail);
    }
}
