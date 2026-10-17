namespace Lumi.Architecture.Tests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumi.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 Lumi.sln，無法定位專案根目錄");
    }
}
