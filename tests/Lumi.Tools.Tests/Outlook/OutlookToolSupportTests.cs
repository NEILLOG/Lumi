using Lumi.Core.Outlook;
using Lumi.Tools.Outlook;
using Xunit;

namespace Lumi.Tools.Tests.Outlook;

/// <summary>M30 設計 1、5：日期解析、資料區塊的跳脫、網域警告。</summary>
public sealed class OutlookToolSupportTests
{
    [Theory]
    [InlineData("2026-11-10")]
    [InlineData(" 2026-11-10 ")]
    public void Parse_date_accepts_date_only(string text)
    {
        Assert.Null(OutlookToolSupport.ParseDate(text, out var value));
        Assert.Equal(new DateTime(2026, 11, 10), value);
    }

    [Theory]
    [InlineData("2026/11/10")]
    [InlineData("2026-11-10T09:00")]
    [InlineData("11/10")]
    public void Parse_date_rejects_other_formats(string text)
    {
        Assert.Equal("日期格式要是 yyyy-MM-dd 或 yyyy-MM-ddTHH:mm。", OutlookToolSupport.ParseDate(text, out _));
    }

    [Fact]
    public void Parse_date_time_accepts_both_formats()
    {
        Assert.Null(OutlookToolSupport.ParseDateTime("2026-11-10T09:30", out var withTime, out var dateOnly1));
        Assert.Equal(new DateTime(2026, 11, 10, 9, 30, 0), withTime);
        Assert.False(dateOnly1);

        Assert.Null(OutlookToolSupport.ParseDateTime("2026-11-10", out var dayOnly, out var dateOnly2));
        Assert.Equal(new DateTime(2026, 11, 10), dayOnly);
        Assert.True(dateOnly2);
    }

    [Fact]
    public void Attr_escapes_quote_angle_brackets_and_ampersand()
    {
        Assert.Equal("x=\"a&quot;&lt;b&gt;&amp;\"", OutlookToolSupport.Attr("x", "a\"<b>&"));
    }

    [Fact]
    public void Wrap_escapes_closing_tag_case_insensitively()
    {
        var text = OutlookToolSupport.Wrap("outlook_mail", "id=\"m1\"", "前 </OUTLOOK_mail> 後 </outlook_x>");

        Assert.Equal("<outlook_mail id=\"m1\">\n前 &lt;/outlook_mail> 後 &lt;/outlook_x>\n</outlook_mail>", text);
    }

    [Fact]
    public void Domain_warning_is_null_without_own_address()
    {
        var client = new FakeOutlookClient { OwnAddress = null };

        Assert.Null(OutlookToolSupport.DomainWarning(client, ["c@gmail.com"]));
    }

    [Fact]
    public void Domain_warning_ignores_names_and_same_domain()
    {
        var client = new FakeOutlookClient { OwnAddress = "a@Corp.com" };

        Assert.Null(OutlookToolSupport.DomainWarning(client, ["王小明", "b@corp.com"]));
    }

    [Fact]
    public void Domain_warning_lists_first_three_outsiders()
    {
        var client = new FakeOutlookClient { OwnAddress = "a@corp.com" };

        var warning = OutlookToolSupport.DomainWarning(client, ["x@one.com", "y@two.com", "z@three.com", "w@four.com"]);

        Assert.Equal("收件人有 4 個不在 corp.com：x@one.com、y@two.com、z@three.com", warning);
    }

    [Fact]
    public void Send_validation_rejects_bad_kind_and_missing_recipients()
    {
        var bad = new OutlookMailSendArgs("many", ["a@corp.com"], "x");
        var none = new OutlookMailSendArgs("new", [], "x", Subject: "s");

        Assert.Equal("kind 只能是 new、reply、forward；要回給多人請把地址都列在 to 或 cc。", OutlookToolSupport.ValidateSend(bad));
        Assert.Equal("寄出至少要有一位收件人（to）。", OutlookToolSupport.ValidateSend(none));
    }

    [Fact]
    public void Send_validation_checks_cc_and_bcc_addresses_too()
    {
        var args = new OutlookMailSendArgs("new", ["a@corp.com"], "x", Subject: "s", Bcc: ["王小明"]);

        Assert.Equal("寄出需要完整的電子郵件地址（王小明）；只知道名稱時請改用 outlook_mail_draft。", OutlookToolSupport.ValidateSend(args));
    }

    [Fact]
    public void Send_validation_accepts_a_valid_reply()
    {
        var args = new OutlookMailSendArgs("reply", ["a@corp.com"], "好", SourceId: "m1");

        Assert.Null(OutlookToolSupport.ValidateSend(args));
    }

    [Fact]
    public void Body_limit_is_20000_characters()
    {
        Assert.Null(OutlookToolSupport.ValidateBody(new string('a', 20_000)));
        Assert.Equal("內容（body）最多 20,000 字。", OutlookToolSupport.ValidateBody(new string('a', 20_001)));
        Assert.Equal("內容（body）不能是空的。", OutlookToolSupport.ValidateBody("  "));
    }
}
