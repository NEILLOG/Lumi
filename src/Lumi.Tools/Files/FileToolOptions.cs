namespace Lumi.Tools;

/// <summary>檔案類工具的上限（M4 計畫「其他我直接定的數字」）。集中在一處，之後要調整改這裡。</summary>
public sealed class FileToolOptions
{
    /// <summary><c>read_file</c> 一次最多回傳的字元數（比對話迴圈的結果上限小一點，工具自己截斷並說明如何繼續讀）。</summary>
    public int MaxReadChars { get; set; } = 18_000;

    /// <summary><c>read_file</c> 預設與最多的行數。</summary>
    public int DefaultReadLines { get; set; } = 2_000;

    public int MaxReadLines { get; set; } = 5_000;

    /// <summary>單行超過這麼多字元就截斷（壓縮過的檔案、超長的 JSON 行）。</summary>
    public int MaxLineChars { get; set; } = 2_000;

    /// <summary><c>read_file</c> 能讀的檔案大小上限；更大的請用 <c>search</c> 找內容。</summary>
    public long MaxReadFileBytes { get; set; } = 10 * 1024 * 1024;

    public int ListMaxEntries { get; set; } = 300;

    public int ListMaxDepth { get; set; } = 3;

    public int SearchMaxFiles { get; set; } = 5_000;

    public long SearchMaxFileBytes { get; set; } = 1024 * 1024;

    public int SearchMaxResults { get; set; } = 100;

    public int SearchMaxPatternLength { get; set; } = 500;

    /// <summary>搜尋結果每一行最多顯示的字元數。</summary>
    public int SearchMaxLineChars { get; set; } = 200;

    // ---- 送出時嵌進訊息的上下文（M4 的 4B） ----

    /// <summary>單一文字檔超過這麼多位元組就不嵌進訊息（只放進範圍，模型用 <c>read_file</c> 讀）。</summary>
    public long EmbedMaxFileBytes { get; set; } = 100 * 1024;

    /// <summary>一則訊息嵌入的檔案文字合計上限；超過的後面幾個同樣只放進範圍。</summary>
    public long EmbedMaxTotalBytes { get; set; } = 200 * 1024;

    /// <summary>資料夾檔案樹的項目數與深度上限。</summary>
    public int TreeMaxEntries { get; set; } = 300;

    public int TreeMaxDepth { get; set; } = 4;
}
