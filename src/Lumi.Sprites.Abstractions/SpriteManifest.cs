using Avalonia;

namespace Lumi.Sprites.Abstractions;

/// <summary>點擊範圍的形狀（在角色框內）。</summary>
public enum SpriteHitShape { Ellipse, Rect }

/// <summary>
/// 角色的描述。程式型角色在程式裡建立，素材型角色（M8）從 <c>manifest.json</c> 載入。
/// </summary>
/// <param name="Box">「中」檔位（直徑 80）的角色框大小（DIP）；其他檔位由殼等比例縮放，並受 <see cref="SpriteLimits"/> 限制。</param>
/// <param name="EffectMargin">特效外擴：光暈、粒子可以畫在框外，每邊最多是框對應邊長的這個比例（0～<see cref="SpriteLimits.EffectMarginMax"/>）。</param>
/// <param name="BubbleAnchor">氣泡接在哪裡，相對於角色框的比例座標（0～1）。</param>
/// <param name="Voice">角色語氣（選填），最多 <see cref="SpriteLimits.VoiceMaxLength"/> 字。</param>
/// <param name="Supported">有自己表現的狀態；沒有列在這裡的狀態會依 <paramref name="Fallback"/> 與預設規則退化。必須包含 <see cref="SpriteState.Idle"/>。</param>
/// <param name="Fallback">覆寫預設的退化規則（<see cref="SpriteStateResolver.DefaultFallback"/>）。</param>
public sealed record SpriteManifest(
    int SchemaVersion,
    string Id,
    string Name,
    string Author,
    string Version,
    Size Box,
    double EffectMargin,
    SpriteHitShape HitShape,
    Point BubbleAnchor,
    string? Voice,
    IReadOnlySet<SpriteState> Supported,
    IReadOnlyDictionary<SpriteState, SpriteState> Fallback)
{
    public const int CurrentSchemaVersion = 1;
}
