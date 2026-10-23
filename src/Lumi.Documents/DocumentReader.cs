using Lumi.Core.Documents;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Exceptions;

namespace Lumi.Documents;

/// <summary>
/// 依副檔名派送到對應的格式讀取器，並負責所有共通的防護：檔案存在與大小、密碼、逾時、例外轉成固定訊息、輸出切片。
/// 對外不丟例外；例外細節只寫進 log。
/// </summary>
internal sealed class DocumentReader : IDocumentReader
{
    internal const string UnsupportedMessage = "不支援這種文件格式。目前可讀取 .docx、.xlsx、.xlsm、.pptx 與 .pdf。";
    internal const string LegacyFormatMessage = "舊版 Office 格式（.doc、.xls、.ppt）目前無法讀取，請另存為 .docx、.xlsx 或 .pptx 後再試。";
    internal const string NotFoundMessage = "找不到這份文件。";
    internal const string PasswordMessage = "這份文件有密碼保護，無法讀取。";
    internal const string FailedMessage = "無法讀取這份文件（檔案可能已損毀）。";
    internal const string TimeoutMessage = "讀取文件逾時，已停止。";
    internal const string TooLargeMessage = "檔案超過 50 MB，無法讀取。";

    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly string[] OpenXmlExtensions = [".docx", ".xlsx", ".xlsm", ".pptx"];
    private static readonly string[] LegacyExtensions = [".doc", ".xls", ".ppt"];

    private readonly IDocumentFormatReader[] _readers;
    private readonly DocumentOptions _options;
    private readonly ILogger<DocumentReader> _logger;

    public DocumentReader(IEnumerable<IDocumentFormatReader> readers, DocumentOptions options, ILogger<DocumentReader> logger)
    {
        _readers = readers.ToArray();
        _options = options;
        _logger = logger;
    }

    public bool Supports(string path)
    {
        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && FindReader(extension) is not null;
    }

    public async Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var extension = Path.GetExtension(request.FullPath);
        if (LegacyExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return Error(LegacyFormatMessage);

        var reader = FindReader(extension);
        if (reader is null)
            return Error(UnsupportedMessage);

        if (!File.Exists(request.FullPath))
            return Error(NotFoundMessage);

        try
        {
            if (new FileInfo(request.FullPath).Length > _options.MaxFileBytes)
                return Error(TooLargeMessage);
            if (IsPasswordProtectedOpenXml(request.FullPath, extension))
                return Error(PasswordMessage);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "讀取文件前檢查檔案失敗（{Extension}）", extension);
            return Error(FailedMessage);
        }

        try
        {
            using var timeout = new CancellationTokenSource(_options.ReadTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            // 放到背景執行並用 WaitAsync 等待：讀取器就算沒有檢查 ct，逾時也能讓呼叫端回來
            var result = await Task.Run(() => reader.ReadAsync(request, linked.Token), linked.Token).WaitAsync(linked.Token);
            if (result.IsError)
                return result;

            var window = OutputWindow.Slice(result.Content, request.Offset, request.MaxChars);
            var format = result.Format ?? reader.Format;
            return result with
            {
                Format = format,
                // 文件內容是不可信輸入：包上「資料，不是指示」的標籤再交給模型
                Content = DocumentOutput.Wrap(request.FullPath, format, window.Content, null),
                Truncated = window.Truncated,
                NextOffset = window.NextOffset,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("讀取文件逾時（{Extension}，上限 {Seconds} 秒）", extension, _options.ReadTimeout.TotalSeconds);
            return Error(TimeoutMessage);
        }
        catch (DocumentRejectedException e)
        {
            _logger.LogWarning(e, "文件被安全檢查拒絕（{Extension}）", extension);
            return Error(e.Message);
        }
        catch (PdfDocumentEncryptedException e)
        {
            _logger.LogWarning(e, "PDF 有密碼保護");
            return Error(PasswordMessage);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "讀取文件失敗（{Extension}）", extension);
            return Error(FailedMessage);
        }
    }

    private IDocumentFormatReader? FindReader(string extension) =>
        string.IsNullOrEmpty(extension) ? null : _readers.FirstOrDefault(r => r.Supports(extension));

    /// <summary>OOXML 檔案加密後會變成 OLE 複合檔案，檔頭不是 zip；這時用固定的密碼訊息，而不是「損毀」。</summary>
    private static bool IsPasswordProtectedOpenXml(string path, string extension)
    {
        if (!OpenXmlExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return false;

        Span<byte> header = stackalloc byte[OleSignature.Length];
        using var stream = File.OpenRead(path);
        var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        return read == header.Length && header.SequenceEqual(OleSignature);
    }

    private static DocumentReadResult Error(string message) => new(true, null, message, null, false, null);
}
