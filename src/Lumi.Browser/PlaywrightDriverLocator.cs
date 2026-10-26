using System.Runtime.InteropServices;

namespace Lumi.Browser;

/// <summary>
/// 單一檔案發佈（M20）時讓 Playwright 找得到 driver（node 與 JS 套件）。
/// 單檔把 <c>.playwright</c> 包在執行檔裡，啟動時和原生函式庫一起解壓到 <c>.net/&lt;程式名稱&gt;/&lt;雜湊&gt;/</c>，
/// 但 Playwright 只在執行檔旁邊找，找不到；解壓出來的 <c>node</c> 在 macOS 也沒有執行權限。
/// 這裡找出解壓資料夾、補上執行權限，並用 <c>PLAYWRIGHT_DRIVER_SEARCH_PATH</c> 指給 Playwright。
/// 一般建置（driver 就在執行檔旁邊）不做任何事。
/// </summary>
public static class PlaywrightDriverLocator
{
    private const string SearchPathVariable = "PLAYWRIGHT_DRIVER_SEARCH_PATH";

    /// <summary>Playwright 的平台資料夾名稱；不支援的平台回 null。</summary>
    public static string? PlatformFolder() =>
        OperatingSystem.IsWindows() ? "win32_x64"
        : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "darwin-arm64" : "darwin-x64")
        : null;

    /// <summary>在 Playwright 建立之前呼叫（可重複呼叫）。找不到解壓資料夾時什麼都不做，讓 Playwright 自己報錯。</summary>
    public static void Prepare()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SearchPathVariable)))
            return;
        var platform = PlatformFolder();
        if (platform is null || File.Exists(NodePath(AppContext.BaseDirectory, platform)))
            return;

        var root = FindExtractionRoot(ExtractionBases(), Path.GetFileNameWithoutExtension(Environment.ProcessPath), platform);
        if (root is null)
            return;
        EnsureExecutable(NodePath(root, platform));
        Environment.SetEnvironmentVariable(SearchPathVariable, root);
    }

    /// <summary>
    /// 在各個解壓根目錄的 <c>&lt;程式名稱&gt;/*/</c> 裡，找含有 <c>.playwright/node/&lt;平台&gt;/node</c> 的資料夾；
    /// 有多個時取最近寫入的（同一個程式名稱的舊版雜湊資料夾不會被誤用）。
    /// </summary>
    internal static string? FindExtractionRoot(IEnumerable<string> bases, string? appName, string platform)
    {
        if (string.IsNullOrEmpty(appName))
            return null;
        return bases
            .Select(b => Path.Combine(b, appName))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateDirectories(d))
            .Where(d => File.Exists(NodePath(d, platform)))
            .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
            .FirstOrDefault();
    }

    /// <summary>.NET 單檔的解壓位置：<c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c>，沒設定時 Windows 是暫存資料夾下的 <c>.net</c>，macOS 是使用者資料夾下的 <c>.net</c>。</summary>
    private static IEnumerable<string> ExtractionBases()
    {
        var custom = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        if (!string.IsNullOrEmpty(custom))
            yield return custom;
        yield return OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), ".net")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".net");
    }

    internal static string NodePath(string root, string platform) =>
        Path.Combine(root, ".playwright", "node", platform, OperatingSystem.IsWindows() ? "node.exe" : "node");

    private static void EnsureExecutable(string nodePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(nodePath))
            return;
        try
        {
            File.SetUnixFileMode(nodePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 設不成功就讓 Playwright 自己報「Permission denied」
        }
    }
}
