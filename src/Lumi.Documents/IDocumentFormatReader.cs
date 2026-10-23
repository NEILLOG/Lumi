using Lumi.Core.Documents;

namespace Lumi.Documents;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名，這裡明確指向 Core 的列舉

/// <summary>一種格式的讀取器。由 <see cref="DocumentReader"/> 依副檔名派送；只回傳完整文字，切片與包裝由上層做。</summary>
internal interface IDocumentFormatReader
{
    DocumentFormat Format { get; }

    /// <param name="extension">含點的副檔名，例如 <c>.docx</c>（大小寫不分）。</param>
    bool Supports(string extension);

    /// <summary>
    /// 讀出完整文字（<see cref="DocumentReadResult.Content"/> 尚未切片）。可以拋出例外，由 <see cref="DocumentReader"/> 轉成固定訊息。
    /// 選擇器（Sheet、Range、Slides、Pages）由讀取器自己解析；語法錯誤回錯誤結果。
    /// </summary>
    Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct);
}
