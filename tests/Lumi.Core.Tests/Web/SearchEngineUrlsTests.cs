using Lumi.Core.Web;

namespace Lumi.Core.Tests.Web;

public class SearchEngineUrlsTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=lumi")]
    [InlineData("https://google.com.tw/search?q=lumi")]
    [InlineData("https://www.google.co.jp/search?q=lumi")]
    [InlineData("https://www.bing.com/search?q=lumi")]
    [InlineData("https://cn.bing.com/search?q=lumi")]
    [InlineData("https://duckduckgo.com/?q=lumi")]
    [InlineData("https://html.duckduckgo.com/html/?q=lumi")]
    [InlineData("https://lite.duckduckgo.com/lite/?q=lumi&kl=tw-tzh")]
    [InlineData("https://search.yahoo.com/search?p=lumi")]
    [InlineData("https://search.yahoo.co.jp/search?p=lumi")]
    [InlineData("https://tw.search.yahoo.com/search?p=lumi")]
    [InlineData("https://www.baidu.com/s?wd=lumi")]
    [InlineData("https://yandex.com/search/?text=lumi")]
    [InlineData("https://yandex.ru/search/?text=lumi")]
    [InlineData("https://search.brave.com/search?q=lumi")]
    [InlineData("https://www.ecosia.org/search?q=lumi")]
    [InlineData("https://www.startpage.com/do/search?q=lumi")]
    [InlineData("https://www.startpage.com/search?q=lumi")]
    [InlineData("HTTPS://WWW.GOOGLE.COM/search?q=lumi")]
    public void Search_pages_are_detected(string url) =>
        Assert.True(SearchEngineUrls.IsSearch(new Uri(url)));

    [Theory]
    [InlineData("https://maps.google.com/search?q=lumi")]
    [InlineData("https://docs.google.com/document/d/abc")]
    [InlineData("https://www.google.com/maps")]
    [InlineData("https://www.bing.com/maps")]
    [InlineData("https://duckduckgo.com/about")]
    [InlineData("https://duckduckgo.com/?ia=web")]
    [InlineData("https://search.yahoo.com/")]
    [InlineData("https://www.baidu.com/")]
    [InlineData("https://www.baidu.com/sports")]
    [InlineData("https://yandex.com/maps")]
    [InlineData("https://search.brave.com/")]
    [InlineData("https://www.ecosia.org/")]
    [InlineData("https://www.startpage.com/about")]
    [InlineData("https://notgoogle.com/search?q=lumi")]
    [InlineData("https://example.com/search?q=lumi")]
    public void Other_pages_are_not_search(string url) =>
        Assert.False(SearchEngineUrls.IsSearch(new Uri(url)));
}
