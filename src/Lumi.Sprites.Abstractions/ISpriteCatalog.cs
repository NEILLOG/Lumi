using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Sprites.Abstractions;

/// <summary>所有已註冊的角色（M8 會把素材型的也加進來）。</summary>
public interface ISpriteCatalog
{
    IReadOnlyList<ISpriteFactory> All { get; }

    ISpriteFactory? Find(string id);
}

public sealed class SpriteCatalog(IEnumerable<ISpriteFactory> factories) : ISpriteCatalog
{
    public IReadOnlyList<ISpriteFactory> All { get; } = [.. factories];

    public ISpriteFactory? Find(string id) => All.FirstOrDefault(f => string.Equals(f.Manifest.Id, id, StringComparison.Ordinal));
}

public static class SpriteServiceCollectionExtensions
{
    /// <summary>註冊一個程式型角色：<c>services.AddSprite&lt;MySpriteFactory&gt;()</c>。</summary>
    public static IServiceCollection AddSprite<TFactory>(this IServiceCollection services) where TFactory : class, ISpriteFactory
    {
        services.AddSingleton<ISpriteFactory, TFactory>();
        services.TryAddSingleton<ISpriteCatalog, SpriteCatalog>();
        return services;
    }
}
