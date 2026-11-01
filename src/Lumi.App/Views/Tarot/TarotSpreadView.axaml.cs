using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 牌陣圖（plan/25 D9、D6b、25B、25D；ui-spec 3.1）。工具結果的回覆附件、凱爾特十字視窗（25F）與點牌放大（25G）共用。
/// 只有「已附加到視覺樹、<see cref="IsActive"/>、<see cref="Spread"/> 不為 null」時才建立牌面與解碼圖片；
/// 其餘情況一律釋放所有 Bitmap 並清空畫布，所以離開畫面的牌陣不佔記憶體。
/// 版面的數字（中心、外框、位置名稱、圓點錨點）全在 <see cref="TarotStageLayout"/>。
/// </summary>
public partial class TarotSpreadView : UserControl
{
    public static readonly StyledProperty<TarotSpreadModel?> SpreadProperty =
        AvaloniaProperty.Register<TarotSpreadView, TarotSpreadModel?>(nameof(Spread));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TarotSpreadView, bool>(nameof(IsActive));

    /// <summary>牌寬（像素）；牌高 = 牌寬 × 12/7。解碼寬度 = 牌寬 × 2（D6b）。</summary>
    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<TarotSpreadView, double>(nameof(CardWidth), 72);

    public static readonly StyledProperty<TarotArrangement> ArrangementProperty =
        AvaloniaProperty.Register<TarotSpreadView, TarotArrangement>(nameof(Arrangement));

    public static readonly StyledProperty<bool> ShowNumbersProperty =
        AvaloniaProperty.Register<TarotSpreadView, bool>(nameof(ShowNumbers));

    public static readonly StyledProperty<bool> ShowCaptionsProperty =
        AvaloniaProperty.Register<TarotSpreadView, bool>(nameof(ShowCaptions));

    /// <summary>高亮的編號（1～10，對應 <see cref="TarotSpreadModel.Cards"/> 的順序）；只更新圓點，不重建牌面。</summary>
    public static readonly StyledProperty<int?> HighlightedIndexProperty =
        AvaloniaProperty.Register<TarotSpreadView, int?>(nameof(HighlightedIndex));

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private bool _attached;
    private readonly List<Bitmap> _bitmaps = [];
    private readonly List<BadgeSlot> _badges = [];

    /// <summary>一個圓點與它的中心（畫布座標）；高亮時依中心重新定位。</summary>
    private sealed record BadgeSlot(TarotNumberBadge Badge, int Number, double CenterX, double CenterY);

    static TarotSpreadView()
    {
        SpreadProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Rebuild());
        IsActiveProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Rebuild());
        CardWidthProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Rebuild());
        ArrangementProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Rebuild());
        ShowNumbersProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Rebuild());
        ShowCaptionsProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Rebuild());
        HighlightedIndexProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.UpdateBadges());
    }

    public TarotSpreadView()
    {
        InitializeComponent();
    }

    /// <summary>要畫的牌陣；null 時不畫。</summary>
    public TarotSpreadModel? Spread
    {
        get => GetValue(SpreadProperty);
        set => SetValue(SpreadProperty, value);
    }

    /// <summary>卡片是否展開（展開才載入）。</summary>
    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>牌寬（像素）。</summary>
    public double CardWidth
    {
        get => GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>排法；<see cref="TarotArrangement.CompactCeltic"/> 是凱爾特十字的對話內縮小版。</summary>
    public TarotArrangement Arrangement
    {
        get => GetValue(ArrangementProperty);
        set => SetValue(ArrangementProperty, value);
    }

    /// <summary>是否在每張牌上畫編號圓點（1～10）；提示前面也會加編號。</summary>
    public bool ShowNumbers
    {
        get => GetValue(ShowNumbersProperty);
        set => SetValue(ShowNumbersProperty, value);
    }

    /// <summary>是否在每張牌下方畫位置名稱。</summary>
    public bool ShowCaptions
    {
        get => GetValue(ShowCaptionsProperty);
        set => SetValue(ShowCaptionsProperty, value);
    }

    /// <summary>高亮的圓點編號；null 表示沒有高亮。</summary>
    public int? HighlightedIndex
    {
        get => GetValue(HighlightedIndexProperty);
        set => SetValue(HighlightedIndexProperty, value);
    }

    /// <summary>左鍵點了一張牌（放開時）。點牌放大在 25G 接線。</summary>
    public event EventHandler<TarotPlacedCard>? CardClicked;

    /// <summary>滑鼠進入某張牌時給那張；離開時給 null。</summary>
    public event EventHandler<TarotPlacedCard?>? CardHovered;

    /// <summary>圖片來源；預設讀內嵌資源，測試可換成假的。</summary>
    public ITarotCardImages Images { get; set; } = TarotCardImages.Default;

    /// <summary>解碼寬度：牌寬 × 2，涵蓋 200% 縮放（D6b）。</summary>
    private int DecodeWidth => (int)Math.Ceiling(CardWidth * 2);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unload();
    }

    /// <summary>依目前的屬性重建畫面；未附加、未展開或沒有牌陣時只清空。</summary>
    private void Rebuild()
    {
        Unload();
        if (_attached && IsActive && Spread is { } spread)
            Load(spread);
    }

    private void Load(TarotSpreadModel spread)
    {
        AutomationProperties.SetName(this, string.Format(Strings.Tarot_SpreadAutomationFormat, spread.SpreadName));
        if (spread.Cards.Count == 0)
            return;

        // 邊框範本整個牌陣只解一次，所有牌面共用（25B 步驟 6）
        var images = Images;
        var frame = Decode(() => images.OpenFrame());
        var window = WindowOf(images);

        var width = CardWidth;
        var height = TarotSpreadLayout.CardHeight(width);
        var layout = TarotStageLayout.Compute(spread.Cards, width, Arrangement, ShowNumbers, ShowCaptions);
        Stage.Width = layout.Width;
        Stage.Height = layout.Height;
        var topZ = spread.Cards.Max(card => card.Z);

        foreach (var p in layout.Cards)
        {
            // 單張圖壞掉只讓這張退回替代牌面，不影響整個牌陣
            var illustration = Decode(() => images.OpenCard(p.Card.CardId));
            var face = new TarotCardFace
            {
                FaceWidth = width,
                Card = p.Card,
                Illustration = illustration,
                Frame = frame,
                Window = window,
            };
            face.Render();

            // 位置的旋轉套在外層（中心旋轉）；逆位的 180° 在牌面內部只轉插畫層，兩者不合併
            var number = ShowNumbers ? p.Number : (int?)null;
            var holder = new Panel
            {
                Width = width,
                Height = height,
                // 透明底才會接收滑鼠事件（游標、提示、點牌）
                Background = Brushes.Transparent,
                Cursor = HandCursor,
                RenderTransform = new RotateTransform(p.Card.Rotation),
                RenderTransformOrigin = RelativePoint.Center,
                ZIndex = p.Card.Z,
            };
            holder.Children.Add(face);
            ToolTip.SetTip(holder, TarotText.Tooltip(p.Card, number));
            var card = p.Card;
            holder.PointerEntered += (_, _) => CardHovered?.Invoke(this, card);
            holder.PointerExited += (_, _) => CardHovered?.Invoke(this, null);
            holder.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left)
                    CardClicked?.Invoke(this, card);
            };
            Canvas.SetLeft(holder, p.CenterX - width / 2);
            Canvas.SetTop(holder, p.CenterY - height / 2);
            Stage.Children.Add(holder);

            if (ShowCaptions)
            {
                var caption = new TarotCaption { Width = layout.CaptionWidth };
                caption.Set(p.Card);
                Canvas.SetLeft(caption, p.CaptionLeft);
                Canvas.SetTop(caption, p.CaptionTop);
                Stage.Children.Add(caption);
            }
        }

        if (ShowNumbers)
        {
            // 圓點在所有牌之上（ZIndex 高於最大的牌層）
            foreach (var p in layout.Cards)
            {
                var badge = new TarotNumberBadge { ZIndex = topZ + 1 };
                ToolTip.SetTip(badge, TarotText.Tooltip(p.Card, p.Number));
                var (cx, cy) = TarotStageLayout.BadgeCenter(p);
                _badges.Add(new BadgeSlot(badge, p.Number, cx, cy));
                Stage.Children.Add(badge);
            }
            UpdateBadges();
        }
    }

    /// <summary>
    /// 依 <see cref="HighlightedIndex"/> 更新圓點的大小與外觀，中心位置不變。只動圓點，不重建牌面。
    /// </summary>
    private void UpdateBadges()
    {
        foreach (var slot in _badges)
        {
            var highlighted = HighlightedIndex == slot.Number;
            slot.Badge.Set(slot.Number, highlighted);
            Canvas.SetLeft(slot.Badge, slot.CenterX - slot.Badge.Width / 2);
            Canvas.SetTop(slot.Badge, slot.CenterY - slot.Badge.Height / 2);
        }
    }

    /// <summary>移除所有牌面與圓點、釋放所有解碼後的圖片。</summary>
    private void Unload()
    {
        Stage.Children.Clear();
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
        _badges.Clear();
        Stage.Width = double.NaN;
        Stage.Height = double.NaN;
    }

    private Bitmap? Decode(Func<Stream?> open)
    {
        var bitmap = TarotImageDecoder.Decode(open, DecodeWidth);
        if (bitmap is not null)
            _bitmaps.Add(bitmap);
        return bitmap;
    }

    internal static TarotWindow WindowOf(ITarotCardImages images)
    {
        try
        {
            return images.FrameWindow ?? TarotWindow.Default;
        }
        catch (Exception)
        {
            return TarotWindow.Default;
        }
    }
}
