using System.Collections.Generic;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Lumi.Tools.Web;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;

namespace Lumi.Tools.Tests.Web;

public sealed class HttpSendToolTests
{
    private readonly FakeWebClient _web = new();
    private readonly HttpSendTool _tool;

    public HttpSendToolTests() => _tool = new HttpSendTool(_web);

    private static ToolContext Ctx() => Context(EmptyFileScope.Instance);

    private Task<ToolResult> Run(object args) => _tool.ExecuteAsync(Json(args), Ctx(), default);

    private ToolPreview Preview(object args) => _tool.Preview(Json(args), Ctx());

    [Fact]
    public void Descriptor_IsDestructive_WorkbenchOnly_SixtySeconds_WebGroup()
    {
        var d = _tool.Descriptor;

        Assert.Equal("http_send", d.Name);
        Assert.Equal("送出資料到網路 API", d.DisplayName);
        Assert.Equal(ToolPermission.Destructive, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.False(d.Modes.Allows(ChatMode.Sprite));
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.Equal(ToolGroups.Web, d.Group);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task EachWriteMethod_IsSentAsIs(string method)
    {
        var result = await Run(new { url = "https://api.example.com/items/1", method, body = "{\"a\":1}" });

        Assert.False(result.IsError);
        var request = Assert.Single(_web.Requests);
        Assert.Equal(method, request.Method);
        Assert.Equal("{\"a\":1}", request.Body);
    }

    [Fact]
    public async Task Get_IsRejected_AndNothingIsSent()
    {
        var result = await Run(new { url = "https://api.example.com/", method = "GET" });

        Assert.True(result.IsError);
        Assert.Contains("POST、PUT、PATCH 或 DELETE", result.Content);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task BodyOverLimit_IsRejected()
    {
        var result = await Run(new { url = "https://api.example.com/", method = "POST", body = new string('x', 100_001) });

        Assert.True(result.IsError);
        Assert.Contains("100,000", result.Content);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task ContentType_DefaultsToJson_WhenNotGiven()
    {
        await Run(new { url = "https://api.example.com/", method = "POST", body = "{}" });

        Assert.Null(Assert.Single(_web.Requests).ContentType);
    }

    [Fact]
    public async Task ContentType_IsPassedThrough_WhenGiven()
    {
        await Run(new { url = "https://api.example.com/", method = "PUT", body = "a=1", content_type = "application/x-www-form-urlencoded" });

        Assert.Equal("application/x-www-form-urlencoded", Assert.Single(_web.Requests).ContentType);
    }

    [Fact]
    public async Task ContentTypeWithNewline_IsRejected()
    {
        var result = await Run(new { url = "https://api.example.com/", method = "POST", content_type = "text/plain\r\nX-Evil: 1" });

        Assert.True(result.IsError);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task ContentTypeHeader_InCustomHeaders_IsRejected()
    {
        var result = await Run(new
        {
            url = "https://api.example.com/",
            method = "POST",
            headers = new Dictionary<string, string> { ["Content-Type"] = "text/plain" },
        });

        Assert.True(result.IsError);
        Assert.Contains("content_type", result.Content);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task Created_IsASuccessfulResult_WithTheStatusSummary()
    {
        _web.Respond = _ => FakeWebClient.Text("application/json", "{\"id\":7}", status: 201);

        var result = await Run(new { url = "https://api.example.com/items", method = "POST", body = "{}" });

        Assert.False(result.IsError);
        Assert.Equal("HTTP 201", result.Summary);
        Assert.Contains("\"id\": 7", result.Content);
    }

    [Fact]
    public async Task WebRequestException_BecomesAFailure()
    {
        _web.Throw = new WebRequestException(WebFailureKind.Timeout, "逾時。");

        var result = await Run(new { url = "https://api.example.com/", method = "DELETE" });

        Assert.True(result.IsError);
        Assert.Equal("逾時", result.Summary);
    }

    // ---- 確認卡片 ----

    [Fact]
    public void Preview_ShowsTheFirst500CharsOfTheBody()
    {
        var body = new string('a', 600);

        var preview = Preview(new { url = "https://api.example.com/items", method = "POST", body });

        Assert.Contains(new string('a', 500) + "…", preview.Detail);
        Assert.DoesNotContain(new string('a', 501), preview.Detail);
        Assert.Contains("內容類型：application/json", preview.Detail);
    }

    [Fact]
    public void Preview_ShowsHeaderNames_NotTheirValues()
    {
        var preview = Preview(new
        {
            url = "https://api.example.com/",
            method = "PATCH",
            headers = new Dictionary<string, string> { ["X-Api-Key"] = "very-secret" },
        });

        Assert.Contains("PATCH https://api.example.com/", preview.Detail);
        Assert.Contains("X-Api-Key", preview.Detail);
        Assert.DoesNotContain("very-secret", preview.Detail);
    }
}
