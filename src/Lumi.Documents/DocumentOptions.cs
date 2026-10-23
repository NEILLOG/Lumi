namespace Lumi.Documents;

/// <summary>讀取文件的上限。預設值就是正式使用的值；測試可以改小（例如逾時測試）。</summary>
public sealed class DocumentOptions
{
    /// <summary>檔案大小上限（位元組）。超過就不開啟，回固定錯誤。</summary>
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>整個讀取（含解析）的逾時。</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
