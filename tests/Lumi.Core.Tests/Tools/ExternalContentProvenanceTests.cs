using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.Tools;

public class ExternalContentProvenanceTests
{
    private static readonly Func<string, bool> NoMcp = _ => false;

    private static ChatMessage AssistantUses(params string[] names) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, names.Select(n => (ContentBlock)new ToolUseBlock(ChatMessage.NewId(), n, "{}")).ToList(),
            DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("web_fetch")]
    [InlineData("web_crawl")]
    [InlineData("web_links")]
    [InlineData("rss_read")]
    [InlineData("http_request")]
    [InlineData("http_send")]
    [InlineData("download_file")]
    [InlineData("browser_snapshot")]
    [InlineData("browser_open")]
    [InlineData("outlook_mail_search")]
    [InlineData("outlook_mail_read")]
    [InlineData("outlook_calendar_list")]
    public void Web_and_browser_tools_are_external(string name)
    {
        Assert.True(ExternalContentProvenance.IsExternal(name, NoMcp));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("run_command")]
    [InlineData("write_file")]
    [InlineData("outlook_mail_draft")]
    [InlineData("outlook_mail_send_to_self")]
    public void Local_tools_are_not_external(string name)
    {
        Assert.False(ExternalContentProvenance.IsExternal(name, NoMcp));
    }

    [Fact]
    public void MCP_tools_are_external()
    {
        Assert.True(ExternalContentProvenance.IsExternal("mcp__weather__forecast", name => name.StartsWith("mcp__", StringComparison.Ordinal)));
        Assert.False(ExternalContentProvenance.IsExternal("mcp__weather__forecast", NoMcp));
    }

    [Fact]
    public void Seen_is_true_when_history_used_an_external_tool()
    {
        var history = new[] { AssistantUses("read_file"), AssistantUses("web_fetch") };

        Assert.True(ExternalContentProvenance.Seen(history, [], NoMcp));
    }

    [Fact]
    public void Seen_is_true_when_only_this_round_used_an_external_tool()
    {
        // 這一輪的助理訊息還沒存進歷史，要靠這一輪已執行的名稱
        var history = new[] { AssistantUses("read_file") };

        Assert.True(ExternalContentProvenance.Seen(history, ["web_fetch"], NoMcp));
    }

    [Fact]
    public void Seen_is_false_for_local_tools_only()
    {
        var history = new[] { AssistantUses("read_file"), AssistantUses("run_command") };

        Assert.False(ExternalContentProvenance.Seen(history, ["run_command"], NoMcp));
    }

    [Fact]
    public void Seen_ignores_user_messages()
    {
        var user = new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("請抓 https://example.com")], DateTimeOffset.UtcNow);

        Assert.False(ExternalContentProvenance.Seen([user], [], NoMcp));
    }
}
