namespace Lumi.Tools.Tests.Files;

/// <summary>測試用的暫存資料夾：每個測試一份，用完整個刪掉。</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "lumi-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path_(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relative, string content = "內容")
    {
        var path = Path_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Dir(string relative)
    {
        var path = Path_(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>建立符號連結；這個環境不允許（例如沒有權限的 Windows）時回傳 false，測試用 Skip 略過。</summary>
    public bool TryLinkFile(string linkRelative, string target)
    {
        try
        {
            var link = Path_(linkRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public bool TryLinkDirectory(string linkRelative, string target)
    {
        try
        {
            var link = Path_(linkRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
