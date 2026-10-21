using Avalonia;
using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Orb;

/// <summary>
/// 光球：程式型角色的參考實作（來源是 <c>poc/Lumi.OrbPoc/OrbControl.cs</c>）。
/// 角色框是光球直徑的 1.2 倍（含一點邊緣的點擊範圍），特效外擴 0.4（光暈與漣漪畫在框外）。
/// </summary>
public sealed class OrbSpriteFactory : ISpriteFactory
{
    public const string SpriteId = "lumi.orb";

    /// <summary>角色框 ÷ 光球直徑。</summary>
    internal const double BoxFactor = 1.2;

    public SpriteManifest Manifest { get; } = new(
        SchemaVersion: SpriteManifest.CurrentSchemaVersion,
        Id: SpriteId,
        Name: "光球",
        Author: "Lumi",
        Version: "1.0.0",
        Box: new Size(SpriteLimits.ReferenceDiameter * BoxFactor, SpriteLimits.ReferenceDiameter * BoxFactor),
        EffectMargin: SpriteLimits.EffectMarginMax,
        HitShape: SpriteHitShape.Ellipse,
        BubbleAnchor: new Point(0.5, 0.08),   // 頭頂
        Voice: null,
        Supported: new HashSet<SpriteState>(Enum.GetValues<SpriteState>()),
        Fallback: new Dictionary<SpriteState, SpriteState>());

    public ISpriteView Create(SpriteOptions options) => new OrbSpriteView(options, Manifest);
}
