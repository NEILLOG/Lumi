using Avalonia;

namespace Lumi.Sprites.Abstractions;

/// <summary>殼偵測到的互動。戳與拖曳的判定（時間、位移門檻）由殼負責，角色只負責表現。</summary>
public abstract record SpriteInteraction
{
    /// <summary>游標靠近或離開。</summary>
    public sealed record Near(bool IsNear) : SpriteInteraction;

    /// <summary>游標相對角色的方向，兩個分量都在 -1..1。</summary>
    public sealed record Look(Vector Direction) : SpriteInteraction;

    /// <summary>在同一點長按。<paramref name="At"/> 的座標原點是 <see cref="ISpriteView.Visual"/> 的左上角。</summary>
    public sealed record PokeBegin(Point At) : SpriteInteraction;

    public sealed record PokeEnd : SpriteInteraction;

    public sealed record DragBegin : SpriteInteraction;

    public sealed record DragEnd : SpriteInteraction;
}
