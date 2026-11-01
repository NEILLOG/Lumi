using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 工具卡片裡的牌陣圖（plan/25 D9、D6b、25B 步驟 2、5、6）。
/// 只有「已附加到視覺樹、<see cref="IsActive"/>、<see cref="Spread"/> 不為 null」時才建立牌面與解碼圖片；
/// 其餘情況一律釋放所有 Bitmap 並清空畫布，所以離開畫面的牌陣不佔記憶體。
/// </summary>
public partial class TarotSpreadView : UserControl
{
    /// <summary>牌寬（像素）。</summary>
    public const double FaceWidth = 72;

    /// <summary>圖片四周留白（像素），避免旋轉的牌角被切掉。</summary>
    private const double Gutter = 12;

    /// <summary>解碼寬度：牌寬 × 2，涵蓋 200% 縮放（D6b）。</summary>
    private const int DecodeWidth = 144;

    public static readonly StyledProperty<TarotSpreadModel?> SpreadProperty =
        AvaloniaProperty.Register<TarotSpreadView, TarotSpreadModel?>(nameof(Spread));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TarotSpreadView, bool>(nameof(IsActive));

    private bool _attached;
    private TarotSpreadModel? _loaded;
    private readonly List<Bitmap> _bitmaps = [];

    static TarotSpreadView()
    {
        SpreadProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Refresh());
        IsActiveProperty.Changed.AddClassHandler<TarotSpreadView>((view, _) => view.Refresh());
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

    /// <summary>圖片來源；預設讀內嵌資源，測試可換成假的。</summary>
    public ITarotCardImages Images { get; set; } = TarotCardImages.Default;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Unload();
    }

    private void Refresh()
    {
        if (!_attached || !IsActive || Spread is not { } spread)
        {
            Unload();
            return;
        }
        if (!ReferenceEquals(_loaded, spread))
            Load(spread);
    }

    private void Load(TarotSpreadModel spread)
    {
        Unload();
        _loaded = spread;
        AutomationProperties.SetName(this, string.Format(Strings.Tarot_SpreadAutomationFormat, spread.SpreadName));
        if (spread.Cards.Count == 0)
            return;

        // 邊框範本整個牌陣只解一次，所有牌面共用（25B 步驟 6）
        var images = Images;
        var frame = Decode(() => images.OpenFrame());
        var window = WindowOf(images);

        // 每張牌的中心與旋轉後的外框；畫布大小取所有外框的範圍加四周留白
        var height = TarotCardFaceMetrics.Height(FaceWidth);
        var placed = spread.Cards.Select(card =>
        {
            var (cx, cy) = TarotSpreadLayout.Center(card.X, card.Y, FaceWidth);
            var (bw, bh) = RotatedBounds(FaceWidth, height, card.Rotation);
            return (Card: card, CenterX: cx, CenterY: cy, BoundsWidth: bw, BoundsHeight: bh);
        }).ToList();
        var minX = placed.Min(p => p.CenterX - p.BoundsWidth / 2);
        var minY = placed.Min(p => p.CenterY - p.BoundsHeight / 2);
        var maxX = placed.Max(p => p.CenterX + p.BoundsWidth / 2);
        var maxY = placed.Max(p => p.CenterY + p.BoundsHeight / 2);
        Stage.Width = maxX - minX + 2 * Gutter;
        Stage.Height = maxY - minY + 2 * Gutter;

        foreach (var p in placed)
        {
            // 單張圖壞掉只讓這張退回替代牌面，不影響整個牌陣
            var illustration = Decode(() => images.OpenCard(p.Card.CardId));
            var face = new TarotCardFace
            {
                FaceWidth = FaceWidth,
                Card = p.Card,
                Illustration = illustration,
                Frame = frame,
                Window = window,
            };
            face.Render();

            // 位置的旋轉套在外層（中心旋轉）；逆位的 180° 在牌面內部只轉插畫層，兩者不合併
            var holder = new Panel
            {
                Width = FaceWidth,
                Height = height,
                RenderTransform = new RotateTransform(p.Card.Rotation),
                RenderTransformOrigin = RelativePoint.Center,
                ZIndex = p.Card.Z,
            };
            holder.Children.Add(face);
            // 牌面的牌名板只放得下中文名，英文名放在提示裡
            ToolTip.SetTip(holder, string.Format(Strings.Tarot_PositionTooltipFormat, p.Card.PositionName, $"{p.Card.CardNameZh} {p.Card.CardNameEn}",
                p.Card.IsReversed ? Strings.Tarot_Reversed : Strings.Tarot_Upright));
            Canvas.SetLeft(holder, p.CenterX - minX + Gutter - FaceWidth / 2);
            Canvas.SetTop(holder, p.CenterY - minY + Gutter - height / 2);
            Stage.Children.Add(holder);
        }
    }

    /// <summary>移除所有牌面、釋放所有解碼後的圖片。</summary>
    private void Unload()
    {
        Stage.Children.Clear();
        foreach (var bitmap in _bitmaps)
            bitmap.Dispose();
        _bitmaps.Clear();
        _loaded = null;
        Stage.Width = double.NaN;
        Stage.Height = double.NaN;
    }

    /// <summary>
    /// 用縮小尺寸解碼（D6b）。任何例外都回 null，由呼叫端退回替代牌面。串流用完即釋放。
    /// 先檢查檔頭：Avalonia 遇到不是圖片的位元組不會丟例外，而是回一張空白圖，所以不檢查就會變成空白牌面。
    /// </summary>
    private Bitmap? Decode(Func<Stream?> open)
    {
        try
        {
            using var source = open();
            if (source is null)
                return null;
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (!HasImageSignature(bytes))
                return null;

            using var decodeStream = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(decodeStream, DecodeWidth);
            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            {
                bitmap.Dispose();
                return null;
            }
            _bitmaps.Add(bitmap);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>PNG、JPEG、WebP 的檔頭；其他位元組一律視為壞圖。</summary>
    private static bool HasImageSignature(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return true;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;
        return bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
    }

    private static TarotWindow WindowOf(ITarotCardImages images)
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

    /// <summary>旋轉後外框的寬高（90° 時寬高互換）。</summary>
    private static (double Width, double Height) RotatedBounds(double width, double height, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        return (width * cos + height * sin, width * sin + height * cos);
    }
}
