namespace Lumi.App.Tests.TestSupport;

/// <summary>repo 裡的柯基範例（<c>samples/sprites/corgi-pixel</c>）：複製進暫存的角色資料夾，當作真的素材型角色用。</summary>
internal sealed class CorgiPack : IDisposable
{
    public CorgiPack()
    {
        Root = Path.Combine(Path.GetTempPath(), "lumi-app-sprites-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Install();
    }

    /// <summary>角色資料夾（裡面一個角色一個子資料夾）。</summary>
    public string Root { get; }

    public string CorgiFolder => Path.Combine(Root, "corgi-pixel");

    public const string CorgiId = "neillog.corgi-pixel";

    private static string Source
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumi.sln")))
                dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("找不到 Lumi.sln"), "samples", "sprites", "corgi-pixel");
        }
    }

    /// <summary>（重新）安裝柯基：把樣本複製進角色資料夾。</summary>
    public void Install()
    {
        Directory.CreateDirectory(CorgiFolder);
        foreach (var file in Directory.GetFiles(Source))
            File.Copy(file, Path.Combine(CorgiFolder, Path.GetFileName(file)), overwrite: true);
    }

    public void Uninstall()
    {
        if (Directory.Exists(CorgiFolder))
            Directory.Delete(CorgiFolder, recursive: true);
    }

    /// <summary>加一個壞掉的角色（沒有 animations）。</summary>
    public void AddBroken(string folder = "broken")
    {
        var dir = Path.Combine(Root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """{ "schemaVersion": 1, "id": "test.broken", "name": "壞掉", "author": "x", "version": "1", "box": { "width": 80, "height": 80 } }""");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
