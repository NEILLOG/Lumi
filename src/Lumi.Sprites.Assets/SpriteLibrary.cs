using Lumi.Sprites.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Sprites.Assets;

public sealed class AssetSpriteOptions
{
    /// <summary>角色資料夾（一個角色一個子資料夾）；null 表示沒有素材型角色。</summary>
    public string? Directory { get; set; }
}

/// <summary>可以重新載入的角色目錄：內建（程式型）角色加上角色資料夾裡的素材型角色。</summary>
public interface IReloadableSpriteCatalog : ISpriteCatalog
{
    /// <summary>角色資料夾的位置（設定頁的「開啟角色資料夾」用）；沒有設定時是 null。</summary>
    string? Directory { get; }

    /// <summary>載入失敗的角色與警告（含載入成功的角色的警告）。</summary>
    IReadOnlyList<SpriteLoadIssue> Issues { get; }

    /// <summary>清單換了（重新載入完成）。在呼叫 <see cref="ReloadAsync"/> 的同步內容上發出。</summary>
    event EventHandler? Changed;

    /// <summary>重新掃描角色資料夾。掃描在背景執行緒，換清單與 <see cref="Changed"/> 回到呼叫者的同步內容。</summary>
    Task ReloadAsync(CancellationToken ct = default);
}

/// <summary>
/// 角色目錄：<see cref="All"/> 與 <see cref="Find"/> 永遠回傳目前的快照（不可變，換的時候整份換），
/// 所以殼與設定頁在 UI 執行緒上讀的時候不會看到載入到一半的狀態。內建角色排前面，素材型角色依 id 排序。
/// </summary>
public sealed class SpriteLibrary : IReloadableSpriteCatalog
{
    private sealed record Snapshot(IReadOnlyList<ISpriteFactory> All, IReadOnlyList<SpriteLoadIssue> Issues);

    private readonly IReadOnlyList<ISpriteFactory> _builtIn;
    private readonly string? _directory;
    private readonly SemaphoreSlim _reloading = new(1, 1);
    private volatile Snapshot _snapshot;

    public SpriteLibrary(IEnumerable<ISpriteFactory> builtIn, AssetSpriteOptions options)
    {
        _builtIn = [.. builtIn];
        _directory = string.IsNullOrWhiteSpace(options.Directory) ? null : options.Directory;
        _snapshot = new Snapshot(_builtIn, []);   // 還沒掃描之前只有內建角色
    }

    public string? Directory => _directory;

    public IReadOnlyList<ISpriteFactory> All => _snapshot.All;

    public IReadOnlyList<SpriteLoadIssue> Issues => _snapshot.Issues;

    public event EventHandler? Changed;

    public ISpriteFactory? Find(string id) => _snapshot.All.FirstOrDefault(f => string.Equals(f.Manifest.Id, id, StringComparison.Ordinal));

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _reloading.WaitAsync(ct);   // 同時按兩次「重新載入」：一個接一個做
        try
        {
            var reserved = _builtIn.Select(f => f.Manifest.Id).ToHashSet(StringComparer.Ordinal);
            var scan = _directory is null
                ? SpriteScanResult.Empty
                : await Task.Run(() => Scan(_directory, reserved), ct);   // 掃描在背景執行緒；下面回到呼叫者的同步內容

            var all = new List<ISpriteFactory>(_builtIn);
            all.AddRange(scan.Sprites.Select(definition => new AssetSpriteFactory(definition)));
            _snapshot = new Snapshot(all, scan.Issues);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _reloading.Release();
        }
    }

    private static SpriteScanResult Scan(string directory, IReadOnlySet<string> reserved)
    {
        try
        {
            return AssetSpriteScanner.Scan(directory, reserved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SpriteScanResult([], [new SpriteLoadIssue(Path.GetFileName(directory), null, $"掃描角色資料夾失敗（{ex.Message}）", true)]);
        }
    }
}

public static class AssetSpriteServiceCollectionExtensions
{
    /// <summary>
    /// 註冊素材型角色：<see cref="SpriteLibrary"/> 取代只有內建角色的 <see cref="ISpriteCatalog"/>，並同時是
    /// <see cref="IReloadableSpriteCatalog"/>。內建角色照常用 <c>AddSprite&lt;T&gt;()</c> 註冊。
    /// </summary>
    public static IServiceCollection AddLumiAssetSprites(this IServiceCollection services, Action<AssetSpriteOptions>? configure = null)
    {
        var options = new AssetSpriteOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);   // 測試要換資料夾：之後用 Replace 換掉這個選項

        services.TryAddSingleton(sp => new SpriteLibrary(sp.GetServices<ISpriteFactory>(), sp.GetRequiredService<AssetSpriteOptions>()));
        services.Replace(ServiceDescriptor.Singleton<ISpriteCatalog>(sp => sp.GetRequiredService<SpriteLibrary>()));
        services.TryAddSingleton<IReloadableSpriteCatalog>(sp => sp.GetRequiredService<SpriteLibrary>());
        return services;
    }
}
