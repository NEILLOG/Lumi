using System.Globalization;
using System.Text;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.App.Services;

/// <summary>
/// 用量明細的 CSV（plan/23-usage-cost.md 第 8 節）：一次 API 請求一列；UTF-8 with BOM（Excel 開啟中文不亂碼）；
/// 換行 CRLF；欄位含逗號、雙引號或換行時整欄加引號，內部的雙引號重複寫（RFC 4180）。
/// 標題列文字寫在這裡的常數（不放 resx，同 M22 工具顯示名稱的慣例）。
/// </summary>
public static class UsageCsvExporter
{
    public const string TimeHeader = "時間";
    public const string TitleHeader = "對話標題";
    public const string PurposeHeader = "用途";
    public const string ModeHeader = "模式";
    public const string ConnectionHeader = "連線";
    public const string ModelHeader = "model ID";
    public const string InputHeader = "未快取輸入";
    public const string CacheReadHeader = "快取讀取";
    public const string CacheWriteHeader = "快取寫入";
    public const string OutputHeader = "輸出";
    public const string EstimatedHeader = "是否估算";
    public const string PriceHeader = "單價快照";
    public const string CostHeader = "費用（US$）";

    public const string PurposeChat = "對話";
    public const string PurposeCompaction = "整理";
    public const string ModeWorkbench = "工作台";
    public const string ModeSprite = "小精靈";
    public const string EstimatedYes = "是";
    public const string FreePrice = "免費";

    private const string LineEnding = "\r\n";
    private const string CostFormat = "0.000000";
    private const string PriceNumberFormat = "0.########";

    private static readonly string[] Headers =
    [
        TimeHeader, TitleHeader, PurposeHeader, ModeHeader, ConnectionHeader, ModelHeader,
        InputHeader, CacheReadHeader, CacheWriteHeader, OutputHeader, EstimatedHeader, PriceHeader, CostHeader,
    ];

    /// <summary>
    /// 寫出標題列與每筆紀錄。<paramref name="records"/> 依時間由舊到新（<see cref="IUsageStore.ListAsync"/> 的順序）。
    /// <paramref name="titles"/> 只放還存在的對話；找不到（已刪除，或沒有對話 id）的寫「（已刪除的對話）」。
    /// </summary>
    public static async Task WriteAsync(Stream output, IReadOnlyList<UsageRecord> records, IReadOnlyDictionary<string, string> titles, TimeZoneInfo zone)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await using var writer = new StreamWriter(output, encoding, leaveOpen: true);
        await writer.WriteAsync(Line(Headers));
        foreach (var record in records)
            await writer.WriteAsync(Line(Row(record, titles, zone)));
        await writer.FlushAsync();
    }

    /// <summary>一列的欄位（順序同 <see cref="Headers"/>）。</summary>
    public static IReadOnlyList<string> Row(UsageRecord record, IReadOnlyDictionary<string, string> titles, TimeZoneInfo zone)
    {
        var title = record.ConversationId is { } id && titles.TryGetValue(id, out var found) ? found : Strings.UsagePage_DeletedConversation;
        var cost = record.CostUsd is { } usd ? usd.ToString(CostFormat, CultureInfo.InvariantCulture) : "";
        return
        [
            LocalTime(record.CreatedAt, zone),
            title,
            record.Purpose == UsagePurpose.Compaction ? PurposeCompaction : PurposeChat,
            record.Mode == ChatMode.Sprite ? ModeSprite : ModeWorkbench,
            record.ConnectionName,
            record.ModelId,
            record.Tokens.InputTokens.ToString(CultureInfo.InvariantCulture),
            record.Tokens.CacheReadInputTokens.ToString(CultureInfo.InvariantCulture),
            record.Tokens.CacheCreationInputTokens.ToString(CultureInfo.InvariantCulture),
            record.Tokens.OutputTokens.ToString(CultureInfo.InvariantCulture),
            record.Estimated ? EstimatedYes : "",
            PriceText(record.Price),
            cost,
        ];
    }

    /// <summary>單價快照：<c>輸入/輸出/快取讀/快取寫</c>（US$ 每百萬 tokens）；免費寫「免費」；當時未設定單價寫空白。</summary>
    public static string PriceText(PriceSnapshot? price)
    {
        if (price is null)
            return "";
        if (price.IsFree)
            return FreePrice;
        return string.Join("/", new[] { price.Input, price.Output, price.CacheRead, price.CacheWrite }
            .Select(p => p.ToString(PriceNumberFormat, CultureInfo.InvariantCulture)));
    }

    private static string LocalTime(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Line(IEnumerable<string> fields) =>
        string.Join(",", fields.Select(Escape)) + LineEnding;

    /// <summary>RFC 4180：含逗號、雙引號、CR 或 LF 的欄位加雙引號，內部的雙引號重複一次。</summary>
    public static string Escape(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? field
            : "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
