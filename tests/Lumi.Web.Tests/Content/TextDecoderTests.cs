using System.Text;
using Lumi.Web.Content;

namespace Lumi.Web.Tests.Content;

public sealed class TextDecoderTests
{
    public TextDecoderTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static byte[] Big5(string text) => Encoding.GetEncoding("big5").GetBytes(text);

    [Fact]
    public void Utf8_bom_wins_over_charset_and_is_removed()
    {
        var body = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("你好")).ToArray();

        Assert.Equal("你好", TextDecoder.Decode(body, "big5", isHtml: false));
    }

    [Fact]
    public void Utf16_bom_is_recognised()
    {
        var body = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("你好")).ToArray();

        Assert.Equal("你好", TextDecoder.Decode(body, null, isHtml: false));
    }

    [Fact]
    public void Header_charset_big5_is_used()
    {
        Assert.Equal("繁體中文", TextDecoder.Decode(Big5("繁體中文"), "big5", isHtml: false));
    }

    [Fact]
    public void Meta_charset_big5_is_used_for_html()
    {
        var body = Big5("<html><head><meta charset=\"big5\"></head><body>繁體中文</body></html>");

        Assert.Contains("繁體中文", TextDecoder.Decode(body, null, isHtml: true));
    }

    [Fact]
    public void Http_equiv_content_type_charset_is_used_for_html()
    {
        var body = Big5("<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=Big5\"></head><body>繁體中文</body></html>");

        Assert.Contains("繁體中文", TextDecoder.Decode(body, null, isHtml: true));
    }

    [Fact]
    public void Meta_charset_is_ignored_for_non_html()
    {
        var body = Encoding.UTF8.GetBytes("<meta charset=\"big5\">你好");

        Assert.Contains("你好", TextDecoder.Decode(body, null, isHtml: false));
    }

    [Fact]
    public void Unknown_charset_falls_back_to_utf8()
    {
        Assert.Equal("你好", TextDecoder.Decode(Encoding.UTF8.GetBytes("你好"), "no-such-charset", isHtml: false));
    }

    [Fact]
    public void Unknown_meta_charset_falls_back_to_utf8()
    {
        var body = Encoding.UTF8.GetBytes("<meta charset=\"no-such-charset\"><p>你好</p>");

        Assert.Contains("你好", TextDecoder.Decode(body, null, isHtml: true));
    }

    [Fact]
    public void Empty_body_is_empty_string()
    {
        Assert.Equal("", TextDecoder.Decode([], "big5", isHtml: true));
    }
}
