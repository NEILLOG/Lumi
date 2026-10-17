namespace Lumi.Infrastructure.Persistence;

public sealed class PersistenceOptions
{
    /// <summary>資料庫檔案路徑；null 表示 <see cref="LumiPaths.DataDirectory"/> 下的 <c>lumi.db</c>。測試用暫存路徑。</summary>
    public string? DatabasePath { get; set; }

    public string ResolveDatabasePath() => DatabasePath ?? Path.Combine(LumiPaths.DataDirectory, "lumi.db");
}
