using Lumi.Core.Mcp;

namespace Lumi.Core.Tests.Mcp;

public class McpServerRulesTests
{
    [Fact]
    public void ValidateName_AcceptsNormalNamesUpToForty()
    {
        Assert.Null(McpServerRules.ValidateName("GitHub"));
        Assert.Null(McpServerRules.ValidateName(new string('名', 40)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateName_RejectsBlank(string? name)
    {
        Assert.Equal("請輸入名稱", McpServerRules.ValidateName(name));
    }

    [Fact]
    public void ValidateName_RejectsMoreThanFortyCharacters()
    {
        Assert.Equal("名稱最多 40 個字", McpServerRules.ValidateName(new string('a', 41)));
    }

    [Fact]
    public void ValidateCommand_RejectsBlankOnly()
    {
        Assert.Null(McpServerRules.ValidateCommand("npx"));
        Assert.Equal("請輸入指令", McpServerRules.ValidateCommand("  "));
        Assert.Equal("請輸入指令", McpServerRules.ValidateCommand(null));
    }

    [Theory]
    [InlineData("https://kb.example.test/mcp")]
    [InlineData("http://localhost:8080/mcp")]
    [InlineData("http://127.0.0.1/mcp")]
    [InlineData("http://[::1]:8080/mcp")]
    public void ValidateUrl_AcceptsHttpsAndLocalHttp(string url)
    {
        Assert.Null(McpServerRules.ValidateUrl(url));
    }

    [Fact]
    public void ValidateUrl_RejectsBlank()
    {
        Assert.Equal("請輸入 URL", McpServerRules.ValidateUrl(" "));
    }

    [Theory]
    [InlineData("kb.example.test/mcp")]
    [InlineData("/mcp")]
    public void ValidateUrl_RejectsRelativeOrHostlessUrls(string url)
    {
        Assert.Equal("請輸入完整的網址", McpServerRules.ValidateUrl(url));
    }

    [Fact]
    public void ValidateUrl_RejectsNonHttpSchemes()
    {
        Assert.Equal("請輸入完整的網址", McpServerRules.ValidateUrl("ftp://kb.example.test/mcp"));
    }

    [Fact]
    public void ValidateUrl_RejectsPlainHttpForRemoteHosts()
    {
        Assert.Equal("遠端伺服器必須使用 https", McpServerRules.ValidateUrl("http://kb.example.test/mcp"));
    }

    [Theory]
    [InlineData("https://user:pw@kb.example.test/mcp")]
    [InlineData("http://user@localhost:8080/mcp")]
    public void ValidateUrl_RejectsCredentialsInUrl(string url)
    {
        Assert.Equal("網址不可包含帳號密碼，請改用標頭", McpServerRules.ValidateUrl(url));
    }

    [Theory]
    [InlineData("github", new[] { "other" })]
    [InlineData("a", new string[0])]
    [InlineData("a-b-c-d-e-f-g-h", new string[0])]
    [InlineData("0123456789abcdef", new string[0])]
    public void ValidateKey_AcceptsValidKeysNotTaken(string key, string[] others)
    {
        Assert.Null(McpServerRules.ValidateKey(key, others));
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("-github")]
    [InlineData("github-")]
    [InlineData("under_score")]
    [InlineData("0123456789abcdefg")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateKey_RejectsBadFormat(string? key)
    {
        Assert.Equal("只能使用小寫英文、數字與 -，最多 16 個字", McpServerRules.ValidateKey(key, []));
    }

    [Fact]
    public void ValidateKey_RejectsDuplicates()
    {
        Assert.Equal("這個代號已經被其他伺服器使用", McpServerRules.ValidateKey("github", ["github"]));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("60")]
    [InlineData("600")]
    public void ValidateTimeout_AcceptsWholeNumbersFromFiveToSixHundred(string value)
    {
        Assert.Null(McpServerRules.ValidateTimeout(value));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("601")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateTimeout_RejectsOthers(string? value)
    {
        Assert.Equal("請輸入 5 到 600 之間的整數", McpServerRules.ValidateTimeout(value));
    }

    [Theory]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("_private")]
    [InlineData("a1")]
    public void ValidateEnvName_AcceptsIdentifiers(string name)
    {
        Assert.Null(McpServerRules.ValidateEnvName(name));
    }

    [Theory]
    [InlineData("1ABC")]
    [InlineData("A-B")]
    [InlineData("A B")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateEnvName_RejectsOthers(string? name)
    {
        Assert.Equal("名稱只能使用英文、數字與底線，不能以數字開頭", McpServerRules.ValidateEnvName(name));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("X-Api-Key")]
    public void ValidateHeaderName_AcceptsTokens(string name)
    {
        Assert.Null(McpServerRules.ValidateHeaderName(name));
    }

    [Theory]
    [InlineData("X Api")]
    [InlineData("X:Api")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateHeaderName_RejectsNonTokens(string? name)
    {
        Assert.Equal("標頭名稱格式不正確", McpServerRules.ValidateHeaderName(name));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("content-length")]
    [InlineData("Mcp-Session-Id")]
    public void ValidateHeaderName_RejectsHeadersLumiSets(string name)
    {
        Assert.Equal("這個標頭由 Lumi 自動設定", McpServerRules.ValidateHeaderName(name));
    }

    [Fact]
    public void ParseArgs_SplitsLinesTrimsAndDropsBlanks()
    {
        var args = McpServerRules.ParseArgs("  -y \r\n\n  @modelcontextprotocol/server-github  \n   \n--flag");

        Assert.Equal(["-y", "@modelcontextprotocol/server-github", "--flag"], args);
    }
}
