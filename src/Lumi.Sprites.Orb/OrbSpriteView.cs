using Avalonia;
using Avalonia.Controls;
using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Orb;

internal sealed class OrbSpriteView : ISpriteView
{
    private readonly OrbControl _orb;
    private readonly SpriteOptions _options;

    public OrbSpriteView(SpriteOptions options, SpriteManifest manifest)
    {
        _options = options;
        _orb = new OrbControl
        {
            OrbDiameter = options.Diameter,
            Width = options.Outer.Width,
            Height = options.Outer.Height,
            IsHitTestVisible = false,   // 點擊由殼用 HitTest 判斷，控制項本身不吃事件
        };
        var box = options.BoxRect;
        BubbleAnchor = new Point(box.X + manifest.BubbleAnchor.X * box.Width, box.Y + manifest.BubbleAnchor.Y * box.Height);
    }

    public Control Visual => _orb;

    public Point BubbleAnchor { get; }

    public int PreferredFps => _orb.PreferredFps;

    public void SetState(SpriteState state) => _orb.SetState(state);

    public void OnInteraction(SpriteInteraction e)
    {
        switch (e)
        {
            case SpriteInteraction.Near near:
                _orb.Near = near.IsNear;
                break;
            case SpriteInteraction.Look look:
                _orb.LookTarget = look.Direction;
                break;
            case SpriteInteraction.PokeBegin poke:
                _orb.BeginPoke(poke.At);
                break;
            case SpriteInteraction.PokeEnd:
                _orb.EndPoke();
                break;
            case SpriteInteraction.DragBegin:
                _orb.Dragging = true;
                break;
            case SpriteInteraction.DragEnd:
                _orb.Dragging = false;
                break;
        }
    }

    public void Advance(TimeSpan dt) => _orb.Advance(dt.TotalSeconds);

    public bool HitTest(Point p) => _options.BoxRect.Contains(p) && _orb.HitOrb(p);
}
