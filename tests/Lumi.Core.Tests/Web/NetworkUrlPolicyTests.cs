using Lumi.Core.Chat;
using Lumi.Core.Web;

namespace Lumi.Core.Tests.Web;

public class NetworkUrlPolicyTests
{
    private static UrlProvenance Provenance(string userText, string? toolContent = null)
    {
        var history = new List<ChatMessage>
        {
            new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(userText)], DateTimeOffset.UnixEpoch),
        };
        if (toolContent is not null)
            history.Add(new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
                [new ToolResultBlock("t", toolContent, false, "x", null, null, 0, ToolApproval.NotRequired)], DateTimeOffset.UnixEpoch));
        return UrlProvenance.From(history, []);
    }

    private static readonly Func<string, bool> NoneApproved = _ => false;

    [Fact]
    public void Site_from_user_text_is_known()
    {
        var check = NetworkUrlPolicy.Check(new Uri("https://a.example/y"), Provenance("https://a.example/x"), NoneApproved);

        Assert.Equal(UrlCheck.Known, check);
    }

    [Fact]
    public void Exact_url_from_tool_result_is_known_but_other_paths_are_unknown()
    {
        var provenance = Provenance("嗨", "https://a.example/x");

        Assert.Equal(UrlCheck.Known, NetworkUrlPolicy.Check(new Uri("https://a.example/x"), provenance, NoneApproved));
        Assert.Equal(UrlCheck.UnknownSite, NetworkUrlPolicy.Check(new Uri("https://a.example/y"), provenance, NoneApproved));
    }

    [Fact]
    public void Unseen_site_is_unknown()
    {
        var check = NetworkUrlPolicy.Check(new Uri("https://b.example/"), Provenance("https://a.example/x"), NoneApproved);

        Assert.Equal(UrlCheck.UnknownSite, check);
    }

    [Fact]
    public void Approved_site_is_known_and_origin_is_passed_in_web_origin_format()
    {
        string? asked = null;

        var check = NetworkUrlPolicy.Check(new Uri("https://b.example/page"), Provenance("嗨"), origin =>
        {
            asked = origin;
            return origin == "https://b.example:443";
        });

        Assert.Equal(UrlCheck.Known, check);
        Assert.Equal("https://b.example:443", asked);
    }

    [Fact]
    public void Search_engine_wins_over_user_pasted_url()
    {
        var url = "https://www.google.com/search?q=lumi";

        var check = NetworkUrlPolicy.Check(new Uri(url), Provenance(url), NoneApproved);

        Assert.Equal(UrlCheck.SearchEngine, check);
    }

    [Fact]
    public void Search_engine_wins_over_approved_site()
    {
        var check = NetworkUrlPolicy.Check(new Uri("https://www.bing.com/search?q=x"), Provenance("嗨"), _ => true);

        Assert.Equal(UrlCheck.SearchEngine, check);
    }

    [Fact]
    public void Warning_texts()
    {
        Assert.Equal("這個網站沒有在對話中出現過：b.example。確認是你要 Lumi 連線的網站再允許。",
            NetworkUrlPolicy.Warning(UrlCheck.UnknownSite, new Uri("https://b.example/page")));
        Assert.Equal("這是搜尋引擎的網址。Lumi 不支援網路搜尋：可能被擋或要求驗證、結果不穩定，查詢內容也會傳給該網站。",
            NetworkUrlPolicy.Warning(UrlCheck.SearchEngine, new Uri("https://www.google.com/search?q=x")));
        Assert.Equal("", NetworkUrlPolicy.Warning(UrlCheck.Known, new Uri("https://a.example/")));
    }

    [Fact]
    public void Web_origin_writes_default_port_and_lowercases()
    {
        Assert.Equal("https://example.com:443", WebOrigin.Of(new Uri("https://Example.COM/a?b=1")));
        Assert.Equal("http://example.com:80", WebOrigin.Of(new Uri("http://example.com")));
        Assert.Equal("http://example.com:8080", WebOrigin.Of(new Uri("http://example.com:8080/x")));
    }
}
