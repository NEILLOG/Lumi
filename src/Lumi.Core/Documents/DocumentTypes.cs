namespace Lumi.Core.Documents;

/// <summary>支援讀取的文件格式（M10 10C）。舊版 Office 格式（.doc／.xls／.ppt）不在內。</summary>
public enum DocumentFormat
{
    Word,
    Excel,
    PowerPoint,
    Pdf,
}

/// <summary>
/// 讀取一份文件的請求。<paramref name="FullPath"/> 已由呼叫端經 <c>IFileScope</c> 解析，這裡不再檢查範圍。
/// <paramref name="Sheet"/>／<paramref name="Range"/> 給 Excel；<paramref name="Slides"/>（PowerPoint）與 <paramref name="Pages"/>（PDF）是 <c>3</c> 或 <c>3-5</c>。
/// <paramref name="Offset"/>／<paramref name="MaxChars"/> 是對「輸出文字」的字元位移與上限（預設 8000，最大 16000）。
/// </summary>
public sealed record DocumentReadRequest(
    string FullPath,
    string? Sheet = null,
    string? Range = null,
    string? Slides = null,
    string? Pages = null,
    bool IncludeNotes = true,
    int Offset = 0,
    int MaxChars = 8000);

/// <summary>
/// 讀取結果。錯誤時 <paramref name="Content"/> 是給模型看的固定繁中訊息（不含路徑與例外細節）。
/// 成功時 <paramref name="Content"/> 已依 Offset／MaxChars 切過；<paramref name="NextOffset"/> 有值表示還有內容可以續讀。
/// </summary>
public sealed record DocumentReadResult(
    bool IsError,
    DocumentFormat? Format,
    string Content,
    string? Summary,
    bool Truncated,
    int? NextOffset);
