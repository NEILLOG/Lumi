namespace Lumi.Core.IO;

/// <summary>
/// 先寫暫存檔再取代，避免寫到一半當機把整個檔案弄壞（1B 的金鑰檔、M2 的設定檔共用）。
/// 不負責序列化同時寫入，呼叫端自己用鎖。
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch (IOException) { /* 留著也無妨，下次寫入會覆蓋 */ }
            }
        }
    }
}
