using Avalonia;

namespace Lumi.Sprites.Abstractions;

/// <summary>
/// 點擊範圍的形狀（一律限制在角色框內）。<see cref="Alpha"/> 是 1.1 新增：依角色目前這一格的透明度判斷，
/// 不透明處（與其周圍一個邏輯像素內）才算命中，適合輪廓不規則的素材型角色。
/// </summary>
public enum SpriteHitShape { Ellipse, Rect, Alpha }

/// <summary>縮放時的取樣方式（1.1 新增）。像素風的角色用 <see cref="Pixelated"/>（最近鄰），不要讓邊緣被平滑掉。</summary>
public enum SpriteRendering { Smooth, Pixelated }

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

    /// <summary>縮放時的取樣方式。選填，預設 <see cref="SpriteRendering.Smooth"/>（1.1 新增，用 <c>init</c> 屬性所以既有的建構不用改）。</summary>
    public SpriteRendering Rendering { get; init; } = SpriteRendering.Smooth;

    /// <summary>
    /// 像素風角色的「邏輯像素數」：每一格在<b>寬度方向</b>有幾個邏輯像素（柯基是 28：每個邏輯像素 10 px、整格 280 px）；
    /// 邏輯像素是正方形，所以高度方向的個數由格子的長寬比決定，必須剛好是整數。
    /// 有宣告時，殼把每個邏輯像素畫成整數個裝置像素，像素粗細才會一致。選填（1.1 新增）。
    /// </summary>
    public int? PixelGrid { get; init; }
}
