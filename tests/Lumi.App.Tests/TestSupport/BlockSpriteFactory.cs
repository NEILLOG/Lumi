using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 「換角色不改殼」用的最小假角色：只畫一個色塊、只支援 <c>Idle</c>。殼完全不認識它是什麼，
/// 其他狀態都走退化，戳與拖曳的互動它收得到但不理會。
/// </summary>
internal sealed class BlockSpriteFactory : ISpriteFactory
{
    public const string SpriteId = "test.block";

    public SpriteManifest Manifest { get; } = new(
        SpriteManifest.CurrentSchemaVersion, SpriteId, "色塊", "測試", "0.1.0", new Size(60, 60), 0.2, SpriteHitShape.Rect, new Point(0.5, 0),
        "句尾加「啦」", new HashSet<SpriteState> { SpriteState.Idle }, new Dictionary<SpriteState, SpriteState>());

    public List<SpriteState> ReceivedStates { get; } = [];

    public ISpriteView Create(SpriteOptions options) => new BlockView(this, options);

    private sealed class BlockView(BlockSpriteFactory owner, SpriteOptions options) : ISpriteView
    {
        public Control Visual { get; } = new Border
        {
            Width = options.Outer.Width,
            Height = options.Outer.Height,
            Child = new Border { Background = Brushes.Crimson, Margin = new Thickness((options.Outer.Width - options.Box.Width) / 2) },
        };

        public Point BubbleAnchor => new(options.Outer.Width / 2, options.BoxRect.Y);
        public int PreferredFps => 30;
        public void SetState(SpriteState state) => owner.ReceivedStates.Add(state);
        public void OnInteraction(SpriteInteraction e) { }
        public void Advance(TimeSpan dt) { }
        public bool HitTest(Point p) => options.BoxRect.Contains(p);
    }
}
