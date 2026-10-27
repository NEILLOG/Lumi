using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lumi.Browser;

/// <summary>
/// 單一檔案發佈（M20、M22）時讓 Playwright 找得到 driver（node 與 JS 套件）。
/// 發佈時 <c>.playwright</c> 不放在執行檔旁邊，而是壓成 zip 內嵌在 Lumi.App 裡（資源名稱 <c>Lumi.Playwright.&lt;SHA-256 前 16 碼&gt;.zip</c>，見 Lumi.App.csproj）；
/// 第一次用到瀏覽器時解到使用者資料夾 <c>Lumi/playwright/&lt;雜湊&gt;/</c>，並用 <c>PLAYWRIGHT_DRIVER_SEARCH_PATH</c> 指給 Playwright。
/// 解壓時先解到同層的暫存資料夾，完成後寫 <c>.complete</c> 標記再改名，解到一半被中斷的不會被誤用；舊雜湊的資料夾留著不刪。
/// 一般建置（driver 就在執行檔旁邊）不會有任何改動，只在 macOS 補 node 的執行權限。
/// </summary>
public static class PlaywrightDriverLocator
{
    private const string SearchPathVariable = "PLAYWRIGHT_DRIVER_SEARCH_PATH";
    private const string ResourcePrefix = "Lumi.Playwright.";
    private const string ResourceSuffix = ".zip";
    internal const string CompleteMarker = ".complete";

    /// <summary>Playwright 的平台資料夾名稱；不支援的平台回 null。</summary>
    public static string? PlatformFolder() =>
        OperatingSystem.IsWindows() ? "win32_x64"
        : OperatingSystem.IsMacOS() ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "darwin-arm64" : "darwin-x64")
        : null;

    /// <summary>內嵌的 driver zip：<paramref name="Hash"/> 是 zip 的 SHA-256 前 16 碼（建置時算好、放在資源名稱裡）。</summary>
    internal sealed record EmbeddedDriver(string Hash, Func<Stream> Open);

    /// <summary>在 Playwright 建立之前呼叫（可重複呼叫）。沒有內嵌的 driver、執行檔旁邊也沒有 node 時什麼都不做，讓 Playwright 自己報錯。</summary>
    public static void Prepare()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SearchPathVariable)))
            return;
        var platform = PlatformFolder();
        if (platform is null)
            return;

        var baseDirectory = AppContext.BaseDirectory;
        if (File.Exists(NodePath(baseDirectory, platform)))
        {
            EnsureExecutable(NodePath(baseDirectory, platform));
            return;
        }

        var embedded = FindEmbedded([Assembly.GetEntryAssembly(), typeof(PlaywrightDriverLocator).Assembly]);
        if (embedded is null)
            return;
        var root = EnsureExtracted(InstallRoot(), embedded, platform);
        Environment.SetEnvironmentVariable(SearchPathVariable, root);
    }

    /// <summary>在給的組件裡找內嵌的 driver zip（取第一個符合名稱格式的）。</summary>
    internal static EmbeddedDriver? FindEmbedded(IEnumerable<Assembly?> assemblies)
    {
        foreach (var assembly in assemblies.OfType<Assembly>().Distinct())
        {
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                    continue;
                var hash = name[ResourcePrefix.Length..^ResourceSuffix.Length];
                if (hash.Length == 0)
                    continue;
                var owner = assembly;
                var resource = name;
                return new EmbeddedDriver(hash, () => owner.GetManifestResourceStream(resource)
                    ?? throw new FileNotFoundException("找不到內嵌的 Playwright driver。", resource));
            }
        }
        return null;
    }

    /// <summary>
    /// 確保 <c>&lt;installRoot&gt;/&lt;雜湊&gt;/</c> 是完整解壓好的 driver 並回傳它的路徑。
    /// 已有 <c>.complete</c> 就直接用；沒有（殘留的半成品）就重解。
    /// </summary>
    internal static string EnsureExtracted(string installRoot, EmbeddedDriver driver, string platform)
    {
        var target = Path.Combine(installRoot, driver.Hash);
        if (IsComplete(target))
        {
            EnsureExecutable(NodePath(target, platform));
            return target;
        }

        Directory.CreateDirectory(installRoot);
        var staging = Path.Combine(installRoot, $"{driver.Hash}.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = driver.Open())
                ZipFile.ExtractToDirectory(stream, staging);
            File.WriteAllText(Path.Combine(staging, CompleteMarker), driver.Hash);
            EnsureExecutable(NodePath(staging, platform));

            if (Directory.Exists(target) && !IsComplete(target))
                Directory.Delete(target, recursive: true);   // 沒有標記的目標是殘留的半成品
            try
            {
                Directory.Move(staging, target);
            }
            catch (IOException) when (IsComplete(target))
            {
                // 另一個 Lumi 程序剛好先解完了：用它的
            }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 殘留的暫存資料夾不影響使用
                }
            }
        }

        return target;
    }

    private static bool IsComplete(string directory) => File.Exists(Path.Combine(directory, CompleteMarker));

    /// <summary>解壓位置：Windows 是 <c>%LOCALAPPDATA%\Lumi\playwright</c>（衍生資料，不跟著漫遊），macOS 是 <c>~/Library/Application Support/Lumi/playwright</c>。</summary>
    private static string InstallRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumi", "playwright")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Lumi", "playwright");

    internal static string NodePath(string root, string platform) =>
        Path.Combine(root, ".playwright", "node", platform, OperatingSystem.IsWindows() ? "node.exe" : "node");

    /// <summary>補上 node 的執行權限（只在 Unix，且還沒有使用者執行權限時）。</summary>
    internal static void EnsureExecutable(string nodePath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(nodePath))
            return;
        try
        {
            var mode = File.GetUnixFileMode(nodePath);
            if (mode.HasFlag(UnixFileMode.UserExecute))
                return;
            File.SetUnixFileMode(nodePath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 設不成功就讓 Playwright 自己報「Permission denied」
        }
    }
}
