namespace Lumi.Core.Documents;

/// <summary>
/// 讀取 Office／PDF 文件並轉成文字（M10 10C）。第一個實作是 <c>Lumi.Documents</c>，只透過這個介面對外。
/// 實作不得拋出例外給呼叫端：讀取失敗一律回 <see cref="DocumentReadResult"/> 的錯誤結果。
/// </summary>
public interface IDocumentReader
{
    /// <summary>只看副檔名（大小寫不分）：.docx .xlsx .xlsm .pptx .pdf 回 true；.doc .xls .ppt 與其他副檔名回 false。</summary>
    bool Supports(string path);

    Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct);
}
