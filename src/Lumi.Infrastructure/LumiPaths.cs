namespace Lumi.Infrastructure;

/// <summary>使用者資料夾的位置（資料庫等）。Windows 與 1B 的 <c>secrets.json</c> 放在同一個資料夾。</summary>
public static class LumiPaths
{
    public static string DataDirectory { get; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumi");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "Lumi");

        // 不在支援範圍，只給測試與開發用
        return Path.Combine(home, ".local", "share", "Lumi");
    }
}
