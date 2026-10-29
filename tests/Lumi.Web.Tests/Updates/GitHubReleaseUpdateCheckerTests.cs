using System.Text;
using Lumi.Core.Updates;
using Lumi.Core.Web;
using Lumi.Web.Updates;
using WebRequest = Lumi.Core.Web.WebRequest;
using WebResponse = Lumi.Core.Web.WebResponse;

namespace Lumi.Web.Tests.Updates;

public sealed class GitHubReleaseUpdateCheckerTests
{
    private sealed class FakeWeb(int status, string body, WebRequestException? failure = null) : IWebClient
    {
        public WebRequest? Request { get; private set; }

        public Task<WebResponse> SendAsync(WebRequest request, CancellationToken ct)
        {
            Request = request;
            if (failure is not null)
                throw failure;
            return Task.FromResult(new WebResponse(status, "", request.Url, "application/json", "utf-8", [], Encoding.UTF8.GetBytes(body), false));
        }

        public Task<WebDownloadResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct) => throw new NotSupportedException();
    }

    private const string Release = """
        {
          "tag_name": "v1.4.0",
          "name": "Lumi 1.4.0",
          "body": "## 新功能\r\n- 開機後自動啟動\r\n- 可以釘選在最上層",
          "html_url": "https://evil.example/phishing",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-10-30T08:00:00Z"
        }
        """;

    [Fact]
    public async Task ReadsTheLatestRelease_FromTheRepositoryEndpoint()
    {
        var web = new FakeWeb(200, Release);

        var info = await new GitHubReleaseUpdateChecker(web).GetLatestAsync(CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/NEILLOG/DesktopAgent/releases/latest", web.Request!.Url.AbsoluteUri);
        Assert.Equal("1.4.0", info!.Version);
        Assert.Equal("Lumi 1.4.0", info.Title);
        Assert.Equal("## 新功能\n- 開機後自動啟動\n- 可以釘選在最上層", info.Notes);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero), info.PublishedAt);
    }

    [Fact]
    public async Task TheDownloadPage_IsBuiltFromTheTag_NotTakenFromTheResponse()
    {
        var info = await new GitHubReleaseUpdateChecker(new FakeWeb(200, Release)).GetLatestAsync(CancellationToken.None);

        Assert.Equal("https://github.com/NEILLOG/DesktopAgent/releases/tag/v1.4.0", info!.PageUrl.AbsoluteUri);
    }

    [Fact]
    public void ATagWithOddCharacters_CannotChangeTheHost()
    {
        var json = """{ "tag_name": "v1.0.0", "name": "", "body": "" }"""u8.ToArray();

        var info = GitHubReleaseUpdateChecker.Parse(json);

        Assert.Equal("github.com", info!.PageUrl.Host);
        Assert.Equal("Lumi 1.0.0", info.Title);   // 沒有名稱時用版本號
        Assert.Equal("", info.Notes);
    }

    [Fact]
    public async Task NoReleaseYet_IsNull()
    {
        var info = await new GitHubReleaseUpdateChecker(new FakeWeb(404, "{}")).GetLatestAsync(CancellationToken.None);

        Assert.Null(info);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v2.0.0", "draft": true }""")]
    [InlineData("""{ "tag_name": "v2.0.0-rc1", "prerelease": true }""")]
    public void DraftsAndPrereleases_AreIgnored(string json) =>
        Assert.Null(GitHubReleaseUpdateChecker.Parse(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    [InlineData("""{ "name": "no tag" }""")]
    public void AnUnusableBody_IsAnUpdateCheckException(string json) =>
        Assert.Throws<UpdateCheckException>(() => GitHubReleaseUpdateChecker.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void LongNotes_AreTrimmed()
    {
        var json = $$"""{ "tag_name": "v1.0.1", "body": "{{new string('a', GitHubReleaseUpdateChecker.MaxNotesLength + 500)}}" }""";

        var info = GitHubReleaseUpdateChecker.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(GitHubReleaseUpdateChecker.MaxNotesLength + 1, info!.Notes.Length);   // 加上結尾的「…」
        Assert.EndsWith("…", info.Notes);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task AServerRefusal_IsAnUpdateCheckException(int status) =>
        await Assert.ThrowsAsync<UpdateCheckException>(() => new GitHubReleaseUpdateChecker(new FakeWeb(status, "{}")).GetLatestAsync(CancellationToken.None));

    [Fact]
    public async Task ANetworkFailure_KeepsTheReadableMessage()
    {
        var web = new FakeWeb(0, "", new WebRequestException(WebFailureKind.NameResolution, "找不到伺服器。"));

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(() => new GitHubReleaseUpdateChecker(web).GetLatestAsync(CancellationToken.None));

        Assert.Equal("找不到伺服器。", ex.Message);
    }
}
