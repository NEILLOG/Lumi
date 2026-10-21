using Avalonia;
using Avalonia.Controls;
using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Assets;

/// <summary>
/// 素材型角色的工廠：包一個載入好的 <see cref="AssetSpriteDefinition"/>，殼與設定頁把它當成任何一個 <see cref="ISpriteFactory"/>
/// （殼不需要知道它是素材）。同一個角色的多個 view（殼與設定頁預覽）共用同一份解碼後的圖片。
/// </summary>
public sealed class AssetSpriteFactory : ISpriteFactory
{
    private readonly SpriteSheetStore _store;

    public AssetSpriteFactory(AssetSpriteDefinition definition)
    {
        Definition = definition;
        _store = new SpriteSheetStore(definition);
    }

    public AssetSpriteDefinition Definition { get; }

    public SpriteManifest Manifest => Definition.Manifest;

    /// <summary>載入時的警告加上解碼時才發現的問題（例如內容貼著格子邊緣）。</summary>
    public IReadOnlyList<SpriteLoadIssue> Issues => [.. Definition.Warnings, .. _store.Issues];

    /// <summary>只有解碼時才發現的問題（動畫第一次用到時才會有）；載入時的警告已經在目錄的 Issues 裡，設定頁用這個避免重複。</summary>
    public IReadOnlyList<SpriteLoadIssue> DecodeIssues => _store.Issues;

    public ISpriteView Create(SpriteOptions options) => new AssetSpriteView(Definition, _store, options);
}

internal sealed class AssetSpriteView : ISpriteView
{
    private readonly AssetSpriteDefinition _definition;
    private readonly SpriteSheetStore _store;
    private readonly SpriteOptions _options;
    private readonly SpriteAnimator _animator;
    private readonly SpriteSheetControl _control;
    private AnimationFrame? _shown;

    public AssetSpriteView(AssetSpriteDefinition definition, SpriteSheetStore store, SpriteOptions options)
    {
        _definition = definition;
        _store = store;
        _options = options;
        _animator = new SpriteAnimator(definition.Animations);
        _control = new SpriteSheetControl(options.Outer, definition.CellPixels, definition.Manifest.PixelGrid, definition.Manifest.Rendering);

        var box = options.BoxRect;
        var anchor = definition.Manifest.BubbleAnchor;
        BubbleAnchor = new Point(box.X + anchor.X * box.Width, box.Y + anchor.Y * box.Height);
        Refresh();
    }

    public Control Visual => _control;

    public Point BubbleAnchor { get; }

    /// <summary>動畫本身的 fps；畫面已經停住（停在最後一格、單格動畫）就降到最低，等狀態或互動事件再叫醒。</summary>
    public int PreferredFps => _animator.IsStatic ? 1 : (int)Math.Clamp(Math.Ceiling(_animator.Fps), 1, AssetSpriteLimits.FpsMax);

    public void SetState(SpriteState state)
    {
        _animator.SetState(state);
        Refresh();
    }

    public void OnInteraction(SpriteInteraction e)
    {
        _animator.OnInteraction(e);
        Refresh();
    }

    public void Advance(TimeSpan dt)
    {
        _animator.Advance(dt);
        Refresh();
    }

    private void Refresh()
    {
        var frame = _animator.Current;
        if (_shown == frame)
            return;
        _shown = frame;

        var animation = _definition.Animations[frame.Key];
        var sheet = _store.Get(animation);
        _control.Show(sheet?.Bitmap, SpriteSheetLayout.SourceRect(_definition.CellPixels, animation.Columns, frame.Index));
    }

    public bool HitTest(Point p)
    {
        var box = _options.BoxRect;
        if (!box.Contains(p))
            return false;   // 角色框外一律不命中

        switch (_definition.Manifest.HitShape)
        {
            case SpriteHitShape.Rect:
                return true;
            case SpriteHitShape.Alpha:
                return AlphaHit(p);
            default:
                var dx = (p.X - box.Center.X) / (box.Width / 2);
                var dy = (p.Y - box.Center.Y) / (box.Height / 2);
                return dx * dx + dy * dy <= 1;
        }
    }

    /// <summary>
    /// 把點換算到目前這一格的來源像素，周圍一個邏輯像素內有不透明像素就算命中。
    /// 柯基是瘦高的輪廓、腳在左右下角：橢圓會漏掉腳，矩形又包含大片透明區、會擋住下方桌面。
    /// </summary>
    private bool AlphaHit(Point p)
    {
        var destination = _control.Destination;
        if (!destination.Contains(p))
            return false;

        var frame = _animator.Current;
        var animation = _definition.Animations[frame.Key];
        if (_store.Get(animation) is not { } sheet)
            return false;

        var cell = _definition.CellPixels;
        var source = SpriteSheetLayout.SourceRect(cell, animation.Columns, frame.Index);
        var sx = (int)Math.Floor((p.X - destination.X) / destination.Width * cell.Width);
        var sy = (int)Math.Floor((p.Y - destination.Y) / destination.Height * cell.Height);
        var radius = Math.Max(1, (int)Math.Round(cell.Width / (_definition.Manifest.PixelGrid ?? 28)));

        for (var y = Math.Max(0, sy - radius); y <= Math.Min((int)cell.Height - 1, sy + radius); y++)
        {
            for (var x = Math.Max(0, sx - radius); x <= Math.Min((int)cell.Width - 1, sx + radius); x++)
            {
                if (sheet.AlphaAt((int)source.X + x, (int)source.Y + y) > 0)
                    return true;
            }
        }
        return false;
    }
}
