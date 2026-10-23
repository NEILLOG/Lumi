using System.IO.Compression;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Lumi.Documents;

/// <summary>zip 容器的上限（防 zip bomb）。預設值就是 C7 的規格；測試可以換成小的數字。</summary>
internal sealed record OpenXmlLimits(long MaxTotalBytes, int MaxCompressionRatio, long RatioCheckMinBytes, int MaxEntries)
{
    public static OpenXmlLimits Default { get; } = new(
        MaxTotalBytes: 200L * 1024 * 1024,
        MaxCompressionRatio: 100,
        RatioCheckMinBytes: 10L * 1024 * 1024,
        MaxEntries: 10_000);
}

/// <summary>
/// 開啟 .docx／.xlsx／.pptx 之前的安全設定（M10 C7）：先檢查 zip 目錄，再用不升級、不載入外部資源的設定開啟；
/// XML 讀取一律禁用 DTD、不解析外部實體、限制字元數。
/// </summary>
internal static class OpenXmlSafety
{
    /// <summary>檢查不通過時給模型看的固定訊息。</summary>
    public const string RejectedMessage = "這份文件的內部結構異常，已拒絕讀取。";

    /// <summary>單一 XML 部件的字元上限（50 MB）。</summary>
    public const long MaxXmlCharacters = 50L * 1024 * 1024;

    /// <summary>檢查 zip 目錄：項目數、解壓後總量、單一項目的壓縮比。失敗丟 <see cref="DocumentRejectedException"/>。</summary>
    public static void Verify(string path, OpenXmlLimits? limits = null)
    {
        using var stream = File.OpenRead(path);
        Verify(stream, limits);
    }

    /// <param name="stream">zip 內容；呼叫結束後不會關閉它。</param>
    public static void Verify(Stream stream, OpenXmlLimits? limits = null)
    {
        limits ??= OpenXmlLimits.Default;
        // 目錄以外的資料不在這裡讀；非 zip 會丟 InvalidDataException，由上層轉成固定的損毀訊息
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        if (zip.Entries.Count > limits.MaxEntries)
            throw new DocumentRejectedException(RejectedMessage);

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            // Length 來自 zip 目錄，是宣告值；真正解壓時 XML 讀取的字元上限是第二道防線
            total += entry.Length;
            if (total > limits.MaxTotalBytes)
                throw new DocumentRejectedException(RejectedMessage);

            if (entry.Length > limits.RatioCheckMinBytes && entry.Length > (long)limits.MaxCompressionRatio * entry.CompressedLength)
                throw new DocumentRejectedException(RejectedMessage);
        }
    }

    /// <summary>開啟文件用的設定：不自動升級到較新的標記相容模式（NoProcess），不自動存檔。</summary>
    public static OpenSettings CreateOpenSettings() => new()
    {
        AutoSave = false,
        MarkupCompatibilityProcessSettings = new MarkupCompatibilityProcessSettings(
            MarkupCompatibilityProcessMode.NoProcess,
            FileFormatVersions.Office2007),
    };

    /// <summary>直接用 XmlReader 讀部件時的設定：禁用 DTD、不解析外部資源、限制字元數。每次呼叫都回新的實例。</summary>
    public static XmlReaderSettings CreateXmlReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxXmlCharacters,
    };
}
