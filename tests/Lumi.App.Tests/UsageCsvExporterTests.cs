using System.Text;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.App.Tests;

/// <summary>設定 › 用量與費用的 CSV（plan/23-usage-cost.md 第 8 節）：BOM、引號跳脫、已刪除的對話、單價快照、本地時間。</summary>
public class UsageCsvExporterTests
{
    private const string Header = "時間,對話標題,用途,模式,連線,model ID,未快取輸入,快取讀取,快取寫入,輸出,是否估算,單價快照,費用（US$）";

    /// <summary>UTC+8（不依賴系統的時區資料庫）。</summary>
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private static async Task<string> WriteAsync(IReadOnlyList<UsageRecord> records, IReadOnlyDictionary<string, string>? titles = null,
        TimeZoneInfo? zone = null)
    {
        await using var stream = new MemoryStream();
        await UsageCsvExporter.WriteAsync(stream, records, titles ?? new Dictionary<string, string>(), zone ?? TimeZoneInfo.Utc);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<byte[]> WriteBytesAsync(IReadOnlyList<UsageRecord> records)
    {
        await using var stream = new MemoryStream();
        await UsageCsvExporter.WriteAsync(stream, records, new Dictionary<string, string>(), TimeZoneInfo.Utc);
        return stream.ToArray();
    }

    /// <summary>去掉 BOM 之後的第一行以外的列（以 CRLF 切開，最後的空字串去掉）。</summary>
    private static string[] Lines(string csv) =>
        csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task StartsWithAUtf8ByteOrderMark_SoExcelReadsChineseCorrectly()
    {
        var bytes = await WriteBytesAsync([]);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    [Fact]
    public async Task HeaderRow_IsTheFixedColumnList_AndLinesEndWithCrLf()
    {
        var csv = await WriteAsync([]);

        Assert.StartsWith("﻿" + Header + "\r\n", csv);
        Assert.Equal(Header, Lines(csv).Single());
    }

    [Fact]
    public async Task OneRowPerRequest_WithTokensCostAndPriceSnapshot()
    {
        var record = UsageSamples.Record(new DateTimeOffset(2026, 10, 10, 3, 4, 5, TimeSpan.Zero), 0.0123456789m,
            conversationId: "c1", tokens: new TokenUsage(1_200, 300, 40, 500));

        var lines = Lines(await WriteAsync([record], new Dictionary<string, string> { ["c1"] = "我的對話" }));

        Assert.Equal(2, lines.Length);
        Assert.Equal("2026-10-10 03:04:05,我的對話,對話,工作台,測試連線,test-model,1200,500,40,300,,3/15/0.3/3.75,0.012346", lines[1]);
    }

    [Fact]
    public async Task TitlesWithCommasQuotesOrNewlines_AreQuotedAndEscaped()
    {
        var record = UsageSamples.Record(UsageSamples.Day(10), 1m, conversationId: "c1");

        var csv = await WriteAsync([record], new Dictionary<string, string> { ["c1"] = "他說 \"好\", 然後\n換行" });

        Assert.Contains("\"他說 \"\"好\"\", 然後\n換行\"", csv);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line\r\nbreak", "\"line\r\nbreak\"")]
    [InlineData("", "")]
    public void Escape_FollowsRfc4180(string field, string expected) =>
        Assert.Equal(expected, UsageCsvExporter.Escape(field));

    [Fact]
    public async Task DeletedOrMissingConversations_SayDeletedInsteadOfATitle()
    {
        var deleted = UsageSamples.Record(UsageSamples.Day(10), 1m, conversationId: "gone");
        var noConversation = UsageSamples.Record(UsageSamples.Day(11), 1m, conversationId: null);

        var lines = Lines(await WriteAsync([deleted, noConversation], new Dictionary<string, string>()));

        Assert.StartsWith("2026-10-10 12:00:00," + Strings.UsagePage_DeletedConversation + ",", lines[1]);
        Assert.StartsWith("2026-10-11 12:00:00," + Strings.UsagePage_DeletedConversation + ",", lines[2]);
    }

    [Fact]
    public async Task FreePrice_IsWrittenAsFreeWithZeroCost()
    {
        var record = UsageSamples.Record(UsageSamples.Day(10), 0m, price: new PriceSnapshot(0, 0, 0, 0, IsFree: true));

        var row = Lines(await WriteAsync([record])).Last();

        Assert.EndsWith(",免費,0.000000", row);
    }

    [Fact]
    public async Task UnpricedRequest_LeavesPriceAndCostBlank()
    {
        var record = UsageSamples.Record(UsageSamples.Day(10), null, price: null);

        var row = Lines(await WriteAsync([record])).Last();

        Assert.EndsWith(",,", row);   // 單價快照與費用都是空白
        Assert.Equal(13, row.Split(',').Length);
    }

    [Fact]
    public async Task EstimatedRows_SayYes_AndCompactionAndSpriteAreLabelled()
    {
        var record = UsageSamples.Record(UsageSamples.Day(10), 1m, estimated: true, purpose: UsagePurpose.Compaction, mode: ChatMode.Sprite);

        var row = Lines(await WriteAsync([record])).Last();
        var fields = row.Split(',');

        Assert.Equal("整理", fields[2]);
        Assert.Equal("小精靈", fields[3]);
        Assert.Equal("是", fields[10]);
    }

    [Fact]
    public async Task Timestamps_AreLocalTime()
    {
        // 2026-10-29 16:30 UTC ＝ 10/30 00:30（UTC+8）
        var record = UsageSamples.Record(new DateTimeOffset(2026, 10, 29, 16, 30, 0, TimeSpan.Zero), 1m);

        var row = Lines(await WriteAsync([record], zone: Taipei)).Last();

        Assert.StartsWith("2026-10-30 00:30:00,", row);
    }

    [Fact]
    public void PriceText_JoinsTheFourRatesWithSlashes()
    {
        Assert.Equal("3/15/0.3/3.75", UsageCsvExporter.PriceText(new PriceSnapshot(3, 15, 0.3m, 3.75m, IsFree: false)));
        Assert.Equal("免費", UsageCsvExporter.PriceText(new PriceSnapshot(0, 0, 0, 0, IsFree: true)));
        Assert.Equal("", UsageCsvExporter.PriceText(null));
    }
}
