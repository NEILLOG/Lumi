using IoPath = System.IO.Path;

namespace Lumi.Documents.Tests.Samples;

/// <summary>測試用的暫存資料夾，Dispose 時整個刪除。</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = IoPath.Combine(IoPath.GetTempPath(), "lumi-documents-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>在資料夾裡寫入 <paramref name="bytes"/>，回傳完整路徑。</summary>
    public string Write(string fileName, byte[] bytes) => SampleFiles.Write(Path, fileName, bytes);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 清不掉就算了（資料夾在系統的 temp 下）
        }
    }
}

/// <summary>樣本檔寫入的共用方法：各 builder 的 <c>Create</c> 都走這裡。</summary>
public static class SampleFiles
{
    /// <summary>把 <paramref name="bytes"/> 寫進 <paramref name="directory"/>，回傳完整路徑。</summary>
    public static string Write(string directory, string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        var full = IoPath.Combine(directory, fileName);
        File.WriteAllBytes(full, bytes);
        return full;
    }
}
