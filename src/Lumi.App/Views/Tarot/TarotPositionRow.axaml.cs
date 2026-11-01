using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>位置對照清單的一行（plan/25 25D、25F；ui-spec 3.1）。</summary>
public partial class TarotPositionRow : UserControl
{
    /// <summary>互動模式的左右內距（ui-spec 3.1 的行 Padding 1,8；上下 1 加上 18 的行高，改用 MinHeight 20 達到同樣高度）。</summary>
    private static readonly Thickness InteractivePadding = new(8, 0);

    /// <summary>互動模式的行高（1 + 18 + 1）。</summary>
    private const double InteractiveMinHeight = 20;

    /// <summary>互動模式的行圓角（ui-spec 3.1：8）。</summary>
    private static readonly CornerRadius InteractiveCornerRadius = new(8);

    private bool _interactive;
    private bool _highlighted;

    public TarotPositionRow()
    {
        InitializeComponent();
    }

    /// <summary>位置編號（1～10）；<see cref="Set"/> 之後才有值。</summary>
    public int PositionNumber { get; private set; }

    /// <summary>
    /// 是否為牌陣視窗的互動模式：有內距、圓角、高度 20 並讓高亮底能畫出來。預設 false（對話內縮小版）。
    /// </summary>
    public bool Interactive
    {
        get => _interactive;
        set
        {
            _interactive = value;
            Root.Padding = value ? InteractivePadding : default;
            Root.MinHeight = value ? InteractiveMinHeight : 0;
            Root.CornerRadius = value ? InteractiveCornerRadius : default;
            Line.VerticalAlignment = value ? Avalonia.Layout.VerticalAlignment.Center : Avalonia.Layout.VerticalAlignment.Stretch;
        }
    }

    /// <summary>高亮（滑鼠在清單或牌上時）：Soft 底，文字改 OnSoft 色。只在互動模式使用。</summary>
    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value)
                return;
            _highlighted = value;
            Root.Classes.Set("highlighted", value);
            Number.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(value ? "LumiTextOnSoftBrush" : "LumiTextBrush"));
            Head.Bind(TextElement.ForegroundProperty, this.GetResourceObservable(value ? "LumiTextOnSoftBrush" : "LumiTextBrush"));
            Tail.Bind(TextElement.ForegroundProperty, this.GetResourceObservable(value ? "LumiTextOnSoftBrush" : "LumiTextMutedBrush"));
        }
    }

    public void Set(int number, TarotPlacedCard card)
    {
        PositionNumber = number;
        Number.Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (position, tail) = TarotText.CaptionParts(card);
        Head.Text = position;
        Tail.Text = tail;
    }
}
