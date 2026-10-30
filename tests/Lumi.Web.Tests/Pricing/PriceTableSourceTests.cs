using System.Text;
using Lumi.Core.Usage;
using Lumi.Core.Web;
using Lumi.Web.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using WebRequest = Lumi.Core.Web.WebRequest;
using WebResponse = Lumi.Core.Web.WebResponse;

namespace Lumi.Web.Tests.Pricing;

public sealed class PriceTableSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumi-price-tests-" + Guid.NewGuid().ToString("N"));

    private string JsonPath => Path.Combine(_dir, PriceTableSource.JsonFileName);
    private string DatePath => Path.Combine(_dir, PriceTableSource.DateFileName);
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeWeb(int status = 200, byte[]? body = null, WebRequestException? failure = null, bool truncated = false) : IWebClient
    {
        public WebRequest? Request { get; private set; }

        public Task<WebResponse> SendAsync(WebRequest request, CancellationToken ct)
        {
            Request = request;
            if (failure is not null)
                throw failure;
            return Task.FromResult(new WebResponse(status, "", request.Url, "application/json", "utf-8", [], body ?? Array.Empty<byte>(), truncated));
        }

        public Task<WebDownloadResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct) => throw new NotSupportedException();
    }

    private static PriceTableSource Create(IWebClient web, string dir) =>
        new(web, NullLogger<PriceTableSource>.Instance, dir);

    /// <summary>n 筆聊天模型條目，鍵名為 prefix-0 … prefix-(n-1)。</summary>
    private static string ChatJson(int count, string prefix = "model") =>
        "{" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $"\"{prefix}-{i}\": {{\"mode\": \"chat\", \"input_cost_per_token\": 0.000001, \"output_cost_per_token\": 0.000002}}")) + "}";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static DateOnly BundledDate()
    {
        using var stream = typeof(PriceTableSource).Assembly.GetManifestResourceStream("Lumi.Web.Pricing.litellm-model-prices.date.txt");
        using var reader = new StreamReader(stream!);
        return DateOnly.ParseExact(reader.ReadToEnd().Trim(), "yyyy-MM-dd");
    }

    private void WriteUserFile(string json, string date)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(JsonPath, json);
        File.WriteAllText(DatePath, date);
    }

    [Fact]
    public void UserFile_IsUsed_WhenValid()
    {
        WriteUserFile(ChatJson(150), "2026-01-02");
        var source = Create(new FakeWeb(), _dir);

        Assert.Equal(150, source.Current.Entries.Count);
        Assert.Equal(new DateOnly(2026, 1, 2), source.Current.SnapshotDate);
    }

    [Fact]
    public void BrokenUserFile_FallsBackToBundled()
    {
        WriteUserFile("{not json", "2026-01-02");
        var source = Create(new FakeWeb(), _dir);

        Assert.True(source.Current.Entries.Count >= 500);
        Assert.Equal(BundledDate(), source.Current.SnapshotDate);
    }

    [Fact]
    public void UserFileWithFewerThan100Entries_FallsBackToBundled()
    {
        WriteUserFile(ChatJson(10), "2026-01-02");
        var source = Create(new FakeWeb(), _dir);

        Assert.True(source.Current.Entries.Count >= 500);
        Assert.Equal(BundledDate(), source.Current.SnapshotDate);
    }

    [Fact]
    public void UserFileWithoutDateFile_FallsBackToBundled()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(JsonPath, ChatJson(150));
        var source = Create(new FakeWeb(), _dir);

        Assert.True(source.Current.Entries.Count >= 500);
    }

    [Fact]
    public async Task Update_Success_WritesFilesAndSwitchesCurrent()
    {
        var web = new FakeWeb(body: Bytes(ChatJson(150, "new")));
        var source = Create(web, _dir);

        var result = await source.UpdateAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Today, result.SnapshotDate);
        Assert.Equal(PriceTableSource.SourceUrl, web.Request!.Url.ToString());
        Assert.Equal(PriceTableSource.MaxDownloadBytes, web.Request.MaxBytes);
        Assert.Equal(Bytes(ChatJson(150, "new")), File.ReadAllBytes(JsonPath));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), File.ReadAllText(DatePath).Trim());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Equal(150, source.Current.Entries.Count);
        Assert.Equal(Today, source.Current.SnapshotDate);
    }

    [Fact]
    public async Task Update_DownloadFails_KeepsExistingFileAndCurrent()
    {
        WriteUserFile(ChatJson(120, "old"), "2026-01-02");
        var before = File.ReadAllBytes(JsonPath);
        var source = Create(new FakeWeb(failure: new WebRequestException(WebFailureKind.Connection, "連線失敗。")), _dir);

        var result = await source.UpdateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(new DateOnly(2026, 1, 2), result.SnapshotDate);
        Assert.Equal(before, File.ReadAllBytes(JsonPath));
        Assert.Equal("2026-01-02", File.ReadAllText(DatePath).Trim());
        Assert.Equal(120, source.Current.Entries.Count);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task Update_HttpError_DoesNotWriteFiles()
    {
        var source = Create(new FakeWeb(status: 500, body: Bytes(ChatJson(150))), _dir);

        var result = await source.UpdateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(JsonPath));
        Assert.False(File.Exists(DatePath));
    }

    [Fact]
    public async Task Update_TooFewEntries_DoesNotOverwrite()
    {
        WriteUserFile(ChatJson(120, "old"), "2026-01-02");
        var before = File.ReadAllBytes(JsonPath);
        var source = Create(new FakeWeb(body: Bytes(ChatJson(10, "new"))), _dir);

        var result = await source.UpdateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(before, File.ReadAllBytes(JsonPath));
        Assert.Equal(120, source.Current.Entries.Count);
    }

    [Fact]
    public async Task Update_Truncated_DoesNotWriteFiles()
    {
        var source = Create(new FakeWeb(body: Bytes(ChatJson(150)), truncated: true), _dir);

        var result = await source.UpdateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(JsonPath));
    }

    [Fact]
    public async Task Update_UnparseableBody_DoesNotWriteFiles()
    {
        var source = Create(new FakeWeb(body: Bytes("{oops")), _dir);

        var result = await source.UpdateAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(JsonPath));
    }
}
