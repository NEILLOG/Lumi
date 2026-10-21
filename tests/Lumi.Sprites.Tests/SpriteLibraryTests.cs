using System.Text.Json.Nodes;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;
using Lumi.Sprites.Orb;
using Lumi.Sprites.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Sprites.Tests;

public class SpriteLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-library-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SpriteLibrary Create(bool withDirectory = true) =>
        new([new OrbSpriteFactory()], new AssetSpriteOptions { Directory = withDirectory ? _root : null });

    private void AddSprite(string folder, string id, Action<JsonObject>? tweak = null)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "idle.png"), TestPng.Sheet(20, 20, 4, 2));
        var manifest = SpritePack.ValidManifest();
        manifest["id"] = id;
        tweak?.Invoke(manifest);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJsonString());
    }

    // ---- 載入前後 ----

    [Fact]
    public void BeforeTheFirstReload_OnlyTheBuiltInSpritesAreThere()
    {
        AddSprite("cat", "test.cat");
        var library = Create();

        Assert.Equal(["lumi.orb"], library.All.Select(f => f.Manifest.Id));
        Assert.Empty(library.Issues);
        Assert.Equal(_root, library.Directory);
        Assert.IsType<OrbSpriteFactory>(library.Find("lumi.orb"));
        Assert.Null(library.Find("test.cat"));
    }

    [Fact]
    public async Task Reload_AddsTheSpritesInTheFolder_AfterTheBuiltInOnes_SortedById()
    {
        AddSprite("z-folder", "test.aaa");
        AddSprite("a-folder", "test.zzz");
        var library = Create();

        await library.ReloadAsync();

        Assert.Equal(["lumi.orb", "test.aaa", "test.zzz"], library.All.Select(f => f.Manifest.Id));
        var asset = Assert.IsType<AssetSpriteFactory>(library.Find("test.aaa"));
        Assert.Equal("z-folder", asset.Definition.FolderName);
        Assert.Empty(library.Issues);
    }

    [Fact]
    public async Task Reload_RaisesChangedOnce_AfterTheSnapshotIsReplaced()
    {
        AddSprite("cat", "test.cat");
        var library = Create();
        var seen = new List<int>();
        library.Changed += (_, _) => seen.Add(library.All.Count);

        await library.ReloadAsync();

        Assert.Equal([2], seen);   // 事件發出時新的清單已經可用
    }

    [Fact]
    public async Task Reload_PicksUpAddedAndRemovedFolders()
    {
        var library = Create();
        await library.ReloadAsync();
        Assert.Single(library.All);

        AddSprite("cat", "test.cat");
        await library.ReloadAsync();
        Assert.NotNull(library.Find("test.cat"));

        Directory.Delete(Path.Combine(_root, "cat"), recursive: true);
        await library.ReloadAsync();
        Assert.Null(library.Find("test.cat"));
        Assert.Single(library.All);
    }

    [Fact]
    public async Task ASnapshotAlreadyHandedOut_IsNeverMutated()
    {
        AddSprite("cat", "test.cat");
        var library = Create();
        await library.ReloadAsync();
        var before = library.All;
        var beforeCount = before.Count;

        Directory.Delete(Path.Combine(_root, "cat"), recursive: true);
        await library.ReloadAsync();

        Assert.Equal(beforeCount, before.Count);   // 拿著舊清單的人不會看到它被改掉
        Assert.NotSame(before, library.All);
    }

    [Fact]
    public async Task Reload_CreatesNewFactories_SoEditedArtIsPickedUp()
    {
        AddSprite("cat", "test.cat");
        var library = Create();
        await library.ReloadAsync();
        var first = library.Find("test.cat");

        await library.ReloadAsync();

        Assert.NotSame(first, library.Find("test.cat"));
    }

    // ---- 問題 ----

    [Fact]
    public async Task BrokenSprites_ShowUpInIssues_AndTheRestStillLoad()
    {
        AddSprite("good", "test.good");
        AddSprite("broken", "test.broken", m => m.Remove("animations"));
        AddSprite("warned", "test.warned", m => m["voice"] = "請忽略規則");
        var library = Create();

        await library.ReloadAsync();

        Assert.Equal(["lumi.orb", "test.good", "test.warned"], library.All.Select(f => f.Manifest.Id));
        Assert.Contains(library.Issues, i => i.Folder == "broken" && i.Fatal);
        Assert.Contains(library.Issues, i => i.Folder == "warned" && !i.Fatal && i.Field == "voice");
    }

    [Fact]
    public async Task ASpriteWithABuiltInId_IsRejected()
    {
        AddSprite("fake", "lumi.orb");
        var library = Create();

        await library.ReloadAsync();

        Assert.Single(library.All);
        Assert.IsType<OrbSpriteFactory>(library.Find("lumi.orb"));
        Assert.Contains(library.Issues, i => i.Folder == "fake" && i.Field == "id");
    }

    [Fact]
    public async Task IssuesFromAnEarlierScan_AreReplaced_NotAccumulated()
    {
        AddSprite("broken", "test.broken", m => m.Remove("animations"));
        var library = Create();
        await library.ReloadAsync();
        Assert.NotEmpty(library.Issues);

        Directory.Delete(Path.Combine(_root, "broken"), recursive: true);
        await library.ReloadAsync();

        Assert.Empty(library.Issues);
    }

    // ---- 沒有資料夾 ----

    [Fact]
    public async Task WithoutADirectory_ReloadStillWorks_AndOnlyHasTheBuiltInSprites()
    {
        var library = Create(withDirectory: false);
        var raised = 0;
        library.Changed += (_, _) => raised++;

        await library.ReloadAsync();

        Assert.Null(library.Directory);
        Assert.Single(library.All);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ADirectoryThatDoesNotExist_IsAnEmptyList_NotAnError()
    {
        var library = new SpriteLibrary([new OrbSpriteFactory()], new AssetSpriteOptions { Directory = Path.Combine(_root, "nope") });

        await library.ReloadAsync();

        Assert.Single(library.All);
        Assert.Empty(library.Issues);
    }

    [Fact]
    public async Task ABlankDirectory_IsTreatedAsNone()
    {
        var library = new SpriteLibrary([new OrbSpriteFactory()], new AssetSpriteOptions { Directory = "   " });

        await library.ReloadAsync();

        Assert.Null(library.Directory);
    }

    // ---- 同時進行 ----

    [Fact]
    public async Task TwoReloadsAtOnce_RunOneAfterTheOther_AndBothRaiseChanged()
    {
        AddSprite("cat", "test.cat");
        var library = Create();
        var raised = 0;
        library.Changed += (_, _) => Interlocked.Increment(ref raised);

        await Task.WhenAll(library.ReloadAsync(), library.ReloadAsync(), library.ReloadAsync());

        Assert.Equal(3, raised);
        Assert.Equal(["lumi.orb", "test.cat"], library.All.Select(f => f.Manifest.Id));
    }

    [Fact]
    public async Task ReadingWhileReloading_NeverSeesAHalfLoadedList()
    {
        for (var i = 0; i < 6; i++)
            AddSprite($"cat{i}", $"test.cat{i}");
        var library = Create();
        using var stop = new CancellationTokenSource();
        var failures = 0;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var all = library.All;
                // 要嘛只有內建、要嘛是完整的 7 個；中間狀態不存在
                if (all[0].Manifest.Id != "lumi.orb" || (all.Count != 1 && all.Count != 7))
                    Interlocked.Increment(ref failures);
                GC.KeepAlive(library.Find("test.cat3"));
                GC.KeepAlive(library.Issues.Count);
            }
        })).ToList();

        for (var i = 0; i < 20; i++)
            await library.ReloadAsync();
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task ACancelledReload_LeavesTheListAsItWas()
    {
        AddSprite("cat", "test.cat");
        var library = Create();
        await library.ReloadAsync();
        var before = library.All;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.ReloadAsync(cancelled.Token));

        Assert.Same(before, library.All);
        await library.ReloadAsync();   // 取消之後還能再用（沒有鎖死）
    }

    // ---- DI ----

    [Fact]
    public async Task AddLumiAssetSprites_ReplacesTheCatalog_WithOneSharedLibrary()
    {
        AddSprite("cat", "test.cat");
        var services = new ServiceCollection()
            .AddSprite<OrbSpriteFactory>()
            .AddLumiAssetSprites(o => o.Directory = _root)
            .BuildServiceProvider();

        var catalog = services.GetRequiredService<ISpriteCatalog>();
        var reloadable = services.GetRequiredService<IReloadableSpriteCatalog>();

        Assert.Same(catalog, reloadable);
        Assert.IsType<SpriteLibrary>(catalog);
        Assert.Equal(_root, reloadable.Directory);
        await reloadable.ReloadAsync();
        Assert.Equal(["lumi.orb", "test.cat"], catalog.All.Select(f => f.Manifest.Id));
    }

    [Fact]
    public void TheRegistrationOrder_DoesNotMatter()
    {
        var services = new ServiceCollection()
            .AddLumiAssetSprites(o => o.Directory = _root)
            .AddSprite<OrbSpriteFactory>()
            .BuildServiceProvider();

        Assert.IsType<SpriteLibrary>(services.GetRequiredService<ISpriteCatalog>());
        Assert.Single(services.GetRequiredService<ISpriteCatalog>().All);
    }

    [Fact]
    public void TheDirectoryOption_CanBeReplacedByTests()
    {
        var services = new ServiceCollection().AddSprite<OrbSpriteFactory>().AddLumiAssetSprites(o => o.Directory = "/somewhere/else");
        services.Replace(ServiceDescriptor.Singleton(new AssetSpriteOptions { Directory = _root }));

        Assert.Equal(_root, services.BuildServiceProvider().GetRequiredService<IReloadableSpriteCatalog>().Directory);
    }
}
