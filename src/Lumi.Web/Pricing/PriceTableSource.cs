using System.Globalization;
using System.Text.Json;
using Lumi.Core.Usage;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;

namespace Lumi.Web.Pricing;

/// <summary>
/// 價目表來源（plan 第 4 節「打包」「更新價目表」）。使用者資料夾的版本優先，讀不了或筆數不足就用內建快照；
/// 更新走 <see cref="IWebClient"/>（經過 Proxy），只有解析成功且筆數足夠才覆蓋，失敗就沿用目前的版本。
/// 資料夾由建構式傳入（整合包傳 <c>LumiPaths.DataDirectory</c>），這個專案不引用 Lumi.Infrastructure。
/// </summary>
internal sealed class PriceTableSource(IWebClient web, ILogger<PriceTableSource> logger, string dataDirectory) : IPriceTableSource
{
    internal const string SourceUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";
    internal const long MaxDownloadBytes = 8L * 1024 * 1024;
    internal const int MinEntries = 100;
    internal const string JsonFileName = "model-prices.json";
    internal const string DateFileName = "model-prices.date.txt";

    private const string BundledJsonResource = "Lumi.Web.Pricing.litellm-model-prices.json";
    private const string BundledDateResource = "Lumi.Web.Pricing.litellm-model-prices.date.txt";
    private const string DateFormat = "yyyy-MM-dd";

    private readonly object _lock = new();
    private PriceTable? _current;

    public PriceTable Current
    {
        get
        {
            lock (_lock)
                return _current ??= Load();
        }
    }

    public async Task<PriceTableUpdateResult> UpdateAsync(CancellationToken ct)
    {
        byte[] body;
        try
        {
            var response = await web.SendAsync(new WebRequest(new Uri(SourceUrl)) { MaxBytes = MaxDownloadBytes }, ct).ConfigureAwait(false);
            if (response.Truncated)
            {
                logger.LogWarning("價目表下載超過 {Max} 位元組，沿用目前的版本。", MaxDownloadBytes);
                return Unchanged();
            }
            if (response.StatusCode is < 200 or >= 300)
            {
                logger.LogWarning("價目表下載回應 {Status}，沿用目前的版本。", response.StatusCode);
                return Unchanged();
            }
            body = response.Body;
        }
        catch (WebRequestException ex)
        {
            logger.LogWarning("價目表下載失敗：{Message}", ex.Message);
            return Unchanged();
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        PriceTable table;
        try
        {
            using var stream = new MemoryStream(body, writable: false);
            table = LiteLlmPriceParser.Parse(stream, today);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "價目表內容無法解析，沿用目前的版本。");
            return Unchanged();
        }

        if (table.Entries.Count < MinEntries)
        {
            logger.LogWarning("價目表只有 {Count} 筆，少於 {Min} 筆，沿用目前的版本。", table.Entries.Count, MinEntries);
            return Unchanged();
        }

        try
        {
            WriteFiles(body, today);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "價目表寫入失敗，沿用目前的版本。");
            return Unchanged();
        }

        lock (_lock)
            _current = table;
        return new PriceTableUpdateResult(true, today);
    }

    private PriceTableUpdateResult Unchanged() => new(false, Current.SnapshotDate);

    private PriceTable Load() => TryLoadUserFile() ?? LoadBundled();

    private PriceTable? TryLoadUserFile()
    {
        var jsonPath = Path.Combine(dataDirectory, JsonFileName);
        var datePath = Path.Combine(dataDirectory, DateFileName);
        if (!File.Exists(jsonPath) || !File.Exists(datePath))
            return null;

        try
        {
            var date = ReadDate(File.ReadAllText(datePath));
            using var stream = File.OpenRead(jsonPath);
            var table = LiteLlmPriceParser.Parse(stream, date);
            if (table.Entries.Count >= MinEntries)
                return table;
            logger.LogWarning("使用者資料夾的價目表只有 {Count} 筆，改用內建版本。", table.Entries.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "使用者資料夾的價目表無法讀取，改用內建版本。");
        }
        return null;
    }

    private PriceTable LoadBundled()
    {
        var assembly = typeof(PriceTableSource).Assembly;
        try
        {
            using var json = assembly.GetManifestResourceStream(BundledJsonResource)
                ?? throw new InvalidOperationException($"找不到內建資源 {BundledJsonResource}。");
            using var dateStream = assembly.GetManifestResourceStream(BundledDateResource)
                ?? throw new InvalidOperationException($"找不到內建資源 {BundledDateResource}。");
            using var reader = new StreamReader(dateStream);
            var table = LiteLlmPriceParser.Parse(json, ReadDate(reader.ReadToEnd()));
            if (table.Entries.Count >= MinEntries)
                return table;
            logger.LogError("內建價目表只有 {Count} 筆。", table.Entries.Count);
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidOperationException)
        {
            logger.LogError(ex, "內建價目表無法讀取。");
        }
        return PriceTable.Empty;
    }

    /// <summary>先寫兩個 .tmp，再逐一改名覆蓋；任何一步失敗都會刪掉剩下的暫存檔，已經存在的檔案不會被寫壞。</summary>
    private void WriteFiles(byte[] json, DateOnly date)
    {
        Directory.CreateDirectory(dataDirectory);
        var jsonPath = Path.Combine(dataDirectory, JsonFileName);
        var datePath = Path.Combine(dataDirectory, DateFileName);
        var jsonTmp = jsonPath + ".tmp";
        var dateTmp = datePath + ".tmp";
        try
        {
            File.WriteAllBytes(jsonTmp, json);
            File.WriteAllText(dateTmp, date.ToString(DateFormat, CultureInfo.InvariantCulture));
            File.Move(jsonTmp, jsonPath, overwrite: true);
            File.Move(dateTmp, datePath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(jsonTmp);
            DeleteIfExists(dateTmp);
        }
    }

    private static DateOnly ReadDate(string text) =>
        DateOnly.ParseExact(text.Trim(), DateFormat, CultureInfo.InvariantCulture);

    private static void DeleteIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暫存檔刪不掉不影響結果（下次更新會覆蓋）
        }
    }
}
