using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lumi.App.Controls;

namespace Lumi.App.Markdown;

/// <summary>段落裡一個連結占的文字範圍（以 <c>TextLayout</c> 的文字位置計）。</summary>
public sealed record LinkRange(int Start, int Length, Uri Uri)
{
    public bool Contains(int position) => position >= Start && position < Start + Length;
}

/// <summary>
/// 段落用的文字區塊：Avalonia 沒有內建可點的超連結 Inline，所以記下每個連結的文字範圍，
/// 點擊時用 <c>TextLayout</c> 的命中測試換算成文字位置，落在範圍內才算點到連結。
/// </summary>
public sealed class MarkdownTextBlock : EmojiTextBlock
{
    private IReadOnlyList<LinkRange> _links = [];

    public IReadOnlyList<LinkRange> Links
    {
        get => _links;
        set
        {
            _links = value;
            // 沒有背景的 TextBlock 不會被滑鼠命中測試（點擊會被下面的 Panel 接走）；有連結的段落要給透明背景才收得到事件
            Background = value.Count > 0 ? Brushes.Transparent : null;
        }
    }

    public event Action<Uri>? LinkClicked;

    /// <summary>某個點（相對於這個控制項）上的連結；沒有就 null。</summary>
    public LinkRange? LinkAt(Point point)
    {
        if (Links.Count == 0)
            return null;

        var layout = TextLayout;
        var p = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        var position = layout.HitTestPoint(p).TextPosition;
        if (Links.FirstOrDefault(l => l.Contains(position)) is not { } link)
            return null;

        // 不採信 HitTestPoint 的 IsInside（多行時結果不一致）：自己確認點落在這個字元的上下左右範圍內，
        // 否則點在一行文字右邊的空白處，會誤判成點到行尾的連結。
        var rect = layout.HitTestTextPosition(position);
        if (p.Y < rect.Top || p.Y > rect.Bottom)
            return null;

        var next = layout.HitTestTextPosition(position + 1);
        double right;
        if (Math.Abs(next.Y - rect.Y) < 0.5 && next.X > rect.X)
        {
            right = next.X;   // 同一行：右邊界是下一個字元的位置
        }
        else
        {
            // 這個字元是行尾：右邊界是這一行文字的結尾
            var line = layout.TextLines.FirstOrDefault(l => position >= l.FirstTextSourceIndex && position < l.FirstTextSourceIndex + l.Length);
            right = line is null ? rect.X : line.Start + line.Width;
        }
        return p.X >= rect.X - 0.5 && p.X <= right ? link : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) is null ? null : new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && LinkAt(e.GetPosition(this)) is { } link)
        {
            e.Handled = true;
            LinkClicked?.Invoke(link.Uri);
        }
    }
}
