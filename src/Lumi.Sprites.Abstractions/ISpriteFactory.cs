namespace Lumi.Sprites.Abstractions;

public interface ISpriteFactory
{
    SpriteManifest Manifest { get; }

    ISpriteView Create(SpriteOptions options);
}
