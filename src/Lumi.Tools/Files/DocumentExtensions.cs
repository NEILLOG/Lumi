namespace Lumi.Tools;

/// <summary>
/// 由 <c>read_document</c> 讀取的 Office／PDF 副檔名（M10 A8／C9）。名單集中在這裡：chip 的說明、快照的說明與 <c>list_dir</c> 的提示都用它。
/// Lumi.Tools 不引用 Lumi.Documents（架構規則），所以這份名單要和 Lumi.Documents 的讀取器支援的格式保持一致。
/// </summary>
internal static class DocumentExtensions
{
    /// <summary>chip 與快照說明的同一句話，讓模型在歷史裡也看得到去哪裡讀。</summary>
    public const string ReadByToolNote = "由模型用 read_document 讀取";

    private static readonly string[] Extensions = [".docx", ".xlsx", ".xlsm", ".pptx", ".pdf"];

    /// <summary>副檔名是 Office 或 PDF 格式（不分大小寫）。</summary>
    public static bool IsDocument(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
