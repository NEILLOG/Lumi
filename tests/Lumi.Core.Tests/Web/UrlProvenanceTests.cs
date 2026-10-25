using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Web;

namespace Lumi.Core.Tests.Web;

public class UrlProvenanceTests
{
    private static ChatMessage Message(ChatRole role, params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UnixEpoch);

    private static ChatMessage UserText(string text) => Message(ChatRole.User, new TextBlock(text));

    private static ToolResultBlock Result(string content) =>
        new("t", content, false, "讀取網頁", null, null, 0, ToolApproval.NotRequired);

    private static ChatMessage ToolMessage(string content) => Message(ChatRole.Tool, Result(content));

    private static ChatMessage Context(string? content) =>
        Message(ChatRole.User, new ContextBlock(ContextKind.File, "a.txt", "/a.txt", content, content is not null));

    private static UrlProvenance From(params ChatMessage[] history) => UrlProvenance.From(history, []);

    private static Uri U(string url) => new(url);

    [Fact]
    public void User_text_url_allows_the_whole_site()
    {
        var provenance = From(UserText("看一下 https://a.example/docs/intro 這頁"));

        Assert.True(provenance.IsKnown(U("https://a.example/docs/intro")));
        Assert.True(provenance.IsKnown(U("https://a.example/other?x=1")));
        Assert.False(provenance.IsKnown(U("https://b.example/docs/intro")));
    }

    [Fact]
    public void User_text_site_is_scheme_and_port_specific()
    {
        var provenance = From(UserText("https://a.example/x"));

        Assert.True(provenance.IsKnown(U("https://a.example:443/y")));   // 預設 port 視為相同
        Assert.False(provenance.IsKnown(U("http://a.example/y")));
        Assert.False(provenance.IsKnown(U("https://a.example:8443/y")));
    }

    [Fact]
    public void Host_case_does_not_matter()
    {
        var provenance = From(UserText("https://A.Example/x"));

        Assert.True(provenance.IsKnown(U("https://a.example/y")));
        Assert.True(provenance.IsKnown(U("https://A.EXAMPLE/y")));
    }

    [Fact]
    public void Tool_result_url_allows_only_the_exact_url()
    {
        var provenance = From(UserText("嗨"), ToolMessage("相關連結：https://evil.example/page?id=1"));

        Assert.True(provenance.IsKnown(U("https://evil.example/page?id=1")));
        Assert.False(provenance.IsKnown(U("https://evil.example/page?id=2")));
        Assert.False(provenance.IsKnown(U("https://evil.example/page")));
        Assert.False(provenance.IsKnown(U("https://evil.example/other")));
    }

    [Fact]
    public void Fragment_is_ignored_for_exact_urls()
    {
        var provenance = From(ToolMessage("見 https://a.example/page?x=1#top"));

        Assert.True(provenance.IsKnown(U("https://a.example/page?x=1#bottom")));
        Assert.True(provenance.IsKnown(U("https://a.example/page?x=1")));
    }

    [Fact]
    public void Exact_url_path_and_query_are_case_sensitive()
    {
        var provenance = From(ToolMessage("https://a.example/Page?Id=1"));

        Assert.True(provenance.IsKnown(U("https://A.example/Page?Id=1")));
        Assert.False(provenance.IsKnown(U("https://a.example/page?Id=1")));
        Assert.False(provenance.IsKnown(U("https://a.example/Page?id=1")));
    }

    [Fact]
    public void Context_block_url_allows_only_the_exact_url()
    {
        var provenance = From(Context("參考 https://docs.example/guide.html 與其他"));

        Assert.True(provenance.IsKnown(U("https://docs.example/guide.html")));
        Assert.False(provenance.IsKnown(U("https://docs.example/other.html")));
    }

    [Fact]
    public void Context_block_without_content_is_ignored()
    {
        var provenance = From(Context(null));

        Assert.False(provenance.IsKnown(U("https://docs.example/guide.html")));
    }

    [Fact]
    public void Assistant_text_and_tool_arguments_do_not_count()
    {
        var provenance = From(
            UserText("嗨"),
            Message(ChatRole.Assistant,
                new TextBlock("可以看 https://assistant.example/a"),
                new ThinkingBlock("想到 https://thinking.example/a", "sig"),
                new ToolUseBlock("u1", "web_fetch", """{"url":"https://args.example/a"}""")));

        Assert.False(provenance.IsKnown(U("https://assistant.example/a")));
        Assert.False(provenance.IsKnown(U("https://thinking.example/a")));
        Assert.False(provenance.IsKnown(U("https://args.example/a")));
    }

    [Fact]
    public void Tool_result_text_in_a_user_role_message_does_not_count_as_user_text()
    {
        // 使用者角色的 ToolResultBlock 不該存在；即使有，也只能當「完整網址」，不能放寬成整個網站
        var provenance = From(Message(ChatRole.User, Result("https://x.example/a")));

        Assert.False(provenance.IsKnown(U("https://x.example/b")));
    }

    [Fact]
    public void Pending_results_count()
    {
        var provenance = UrlProvenance.From([UserText("嗨")], [Result("下一頁：https://next.example/p2")]);

        Assert.True(provenance.IsKnown(U("https://next.example/p2")));
        Assert.False(provenance.IsKnown(U("https://next.example/p3")));
    }

    [Fact]
    public void Markdown_link_url_is_extracted_without_the_parenthesis()
    {
        var provenance = From(ToolMessage("[文件](https://a.example/b)"));

        Assert.True(provenance.IsKnown(U("https://a.example/b")));
        Assert.False(provenance.IsKnown(U("https://a.example/b)")));   // 右括號不屬於網址
    }

    [Theory]
    [InlineData("看 https://a.example/b。", "https://a.example/b")]
    [InlineData("看 https://a.example/b, 然後", "https://a.example/b")]
    [InlineData("真的嗎？https://a.example/b?", "https://a.example/b")]
    [InlineData("（https://a.example/b）", "https://a.example/b")]
    [InlineData("「https://a.example/b」", "https://a.example/b")]
    [InlineData("(see https://a.example/b)", "https://a.example/b")]
    [InlineData("https://a.example/b；https://c.example/d", "https://a.example/b")]
    [InlineData("看https://a.example/b，然後", "https://a.example/b")]
    [InlineData("<https://a.example/b>", "https://a.example/b")]
    [InlineData("\"https://a.example/b\"", "https://a.example/b")]
    public void Trailing_punctuation_and_brackets_are_stripped(string text, string expectedFirst)
    {
        // 工具結果只認完整網址：結尾標點若被當成網址的一部分，這裡就對不上
        var provenance = UrlProvenance.From([ToolMessage(text)], []);

        Assert.True(provenance.IsKnown(U(expectedFirst)));
        Assert.False(provenance.IsKnown(U(expectedFirst + "/extra")));
    }

    [Fact]
    public void Trailing_dot_is_not_part_of_the_exact_url()
    {
        var provenance = From(ToolMessage("見 https://a.example/page."));

        Assert.True(provenance.IsKnown(U("https://a.example/page")));
        Assert.False(provenance.IsKnown(U("https://a.example/page.")));
    }

    [Fact]
    public void Text_without_http_urls_yields_nothing()
    {
        var provenance = From(UserText("沒有網址，只有 ftp://a.example/x 與 example.com"), ToolMessage("也沒有"));

        Assert.False(provenance.IsKnown(U("https://a.example/x")));
        Assert.False(provenance.IsKnown(U("https://example.com/")));
    }
}
