namespace Lumi.Documents;

/// <summary>
/// 讀取文件用的開檔方式。Windows 上 Excel、Word、PowerPoint 開著檔案時會持有寫入鎖，
/// 所以讀取端一律允許別人同時讀、寫、刪除（FX-11）；只用 <c>File.OpenRead</c>（只允許別人讀）會被拒絕。
/// </summary>
internal static class DocumentFiles
{
    /// <summary>以唯讀開啟，並允許其他程式同時讀取、寫入、刪除（Office 開著檔案時仍可讀）。</summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>網路路徑（<c>\\server\share\...</c>）；裝置路徑（<c>\\?\</c>、<c>\\.\</c>）不算。</summary>
    public static bool IsNetworkPath(string path) =>
        (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        && !(path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal));
}
