namespace Lumi.Tools.Notes;

/// <summary>筆記與對話搜尋的上限（M10 10B 的「其他我直接定的數字」）。集中在一處，之後要調整改這裡。</summary>
public sealed class NoteOptions
{
    /// <summary><c>add_note</c> 單則內容的字數上限（沿用舊版）。</summary>
    public int MaxContentChars { get; set; } = 4_000;

    /// <summary>分類的字數上限（合併空白、去掉 <c>[</c> <c>]</c> 之後）。</summary>
    public int MaxCategoryChars { get; set; } = 50;

    /// <summary><c>add_note</c> 能記到今天往前幾天。</summary>
    public int MaxPastDays { get; set; } = 30;

    /// <summary><c>add_note</c> 能記到今天往後幾天（明天）。</summary>
    public int MaxFutureDays { get; set; } = 1;

    /// <summary><c>read_notes</c> 一次讀取的最多天數（含起訖日）。</summary>
    public int MaxRangeDays { get; set; } = 31;

    /// <summary><c>read_notes</c> 關鍵字的字數上限。</summary>
    public int MaxKeywordChars { get; set; } = 50;

    /// <summary>單一筆記檔的大小上限；超過的檔案整個略過（沿用舊版）。</summary>
    public long MaxFileBytes { get; set; } = 256 * 1024;

    /// <summary><c>conversation_search</c> 查詢字串的字數上限。</summary>
    public int MaxSearchQueryChars { get; set; } = 100;

    /// <summary><c>conversation_search</c> 合併後最多回傳的筆數。</summary>
    public int MaxSearchResults { get; set; } = 10;

    /// <summary><c>conversation_search</c> 搜尋筆記的天數（沿用舊版的一年）。</summary>
    public int SearchNoteDays { get; set; } = 365;

    /// <summary><c>conversation_search</c> 向對話儲存層要的筆數（要多拿幾筆，因為會排除目前這則對話）。</summary>
    public int SearchConversationScanLimit { get; set; } = 50;

    /// <summary><c>conversation_search</c> 每筆內容片段的字數上限。</summary>
    public int MaxSnippetChars { get; set; } = 1_000;

    /// <summary>確認卡片上顯示內容前幾個字。</summary>
    public int PreviewChars { get; set; } = 80;
}
