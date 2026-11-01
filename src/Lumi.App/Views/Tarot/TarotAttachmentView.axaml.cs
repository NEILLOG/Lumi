using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Lumi.App.ViewModels;
using Lumi.Tarot;
using System.Runtime.CompilerServices;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 塔羅牌陣附件（plan/25 25D；ui-spec 3.1、9.4）。依牌陣 id 決定牌寬與排法：
/// 單張 120、三張 96（都有位置名稱）；凱爾特十字 56、對話內縮小版（編號圓點＋右側對照清單）；其他 72。
/// 凱爾特十字的外框放不下（<c>外框比可用寬度寬</c>）時，清單移到牌陣下方改兩欄。
/// </summary>
public partial class TarotAttachmentView : UserControl
{
    private const string CelticCrossId = "celtic_cross";

    /// <summary>並排時牌陣與清單的間距（ui-spec 3.1：32）。</summary>
    private const double InlineListGap = 32;

    /// <summary>改排時牌陣與清單的間距（ui-spec 3.1：16）。</summary>
    private const double NarrowListGap = 16;

    /// <summary>從改排切回並排時要多出的寬度，避免寬度在臨界點來回切換（遲滯）。</summary>
    private const double NarrowHysteresis = 8;

    private TarotSpreadModel? _spread;
    private bool _celtic;
    private bool _narrow;

    /// <summary>牌陣畫布的寬度（像素，已含留白與位置名稱）。</summary>
    private double _stageWidth;

    /// <summary>並排時外框內容需要的寬（牌陣＋間距＋清單，不含外框的內距）；量過一次後記住，改排時用它判斷能不能切回。</summary>
    private double? _inlineContentWidth;

    public TarotAttachmentView()
    {
        InitializeComponent();
        Spread.CardClicked += (_, card) => TarotCardViewer.Open(card, this, Spread.Images);
    }

    /// <summary>按下「在視窗中開啟牌陣」（只有凱爾特十字有）。</summary>
    public event EventHandler<TarotSpreadModel>? OpenWindowRequested;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Apply((DataContext as TarotAttachmentViewModel)?.Spread);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var available = availableSize.Width;
        var bounded = !double.IsInfinity(available);
        if (bounded)
        {
            // 牌陣最寬只到外框內容欄（超出就在牌陣自己的水平捲動區捲）
            Spread.MaxWidth = Math.Max(0, available - ChromeWidth());
            if (_celtic && _inlineContentWidth is { } inline)
            {
                var needed = inline + ChromeWidth();
                if (!_narrow && needed > available)
                    SetNarrow(true);
                else if (_narrow && available >= needed + NarrowHysteresis)
                    SetNarrow(false);
            }
        }

        var size = base.MeasureOverride(availableSize);
        if (_celtic && !_narrow)
        {
            // 並排量好了：記下需要的寬。第一次量時還沒判斷過，放不下就立刻改排並再量一次
            _inlineContentWidth = _stageWidth + InlineListGap + List.DesiredSize.Width;
            if (bounded && _inlineContentWidth + ChromeWidth() > available)
            {
                SetNarrow(true);
                InvalidateMeasure();
            }
        }
        return size;
    }

    /// <summary>外框的左右內距與邊框（不含內容）；Narrow 時內距由 RWD 樣式改成 12。</summary>
    private double ChromeWidth() =>
        Frame.Padding.Left + Frame.Padding.Right + Frame.BorderThickness.Left + Frame.BorderThickness.Right;

    /// <summary>依牌陣設定牌寬、排法與清單；牌陣放最後，讓它只載入一次。</summary>
    private void Apply(TarotSpreadModel? spread)
    {
        _spread = spread;
        var id = spread?.SpreadId ?? string.Empty;
        _celtic = id == CelticCrossId;
        var (width, arrangement, numbers, captions) = id switch
        {
            "single_card" => (120.0, TarotArrangement.Original, false, true),
            "three_card_timeline" => (96.0, TarotArrangement.Original, false, true),
            CelticCrossId => (56.0, TarotArrangement.CompactCeltic, true, false),
            _ => (72.0, TarotArrangement.Original, false, false),
        };

        Spread.CardWidth = width;
        Spread.Arrangement = arrangement;
        Spread.ShowNumbers = numbers;
        Spread.ShowCaptions = captions;
        Spread.Spread = spread;

        _stageWidth = spread is null
            ? 0
            : TarotStageLayout.Compute(spread.Cards, width, arrangement, numbers, captions).Width;
        _inlineContentWidth = null;
        _narrow = false;
        List.Spread = _celtic ? spread : null;
        List.TwoColumns = false;
        List.IsVisible = _celtic;
        OpenWindowButton.IsVisible = _celtic;
        ApplyLayout();
    }

    /// <summary>Narrow 時清單移到牌陣下方（兩欄、置中）；並排時在右側（單欄、垂直置中）。</summary>
    private void SetNarrow(bool narrow)
    {
        _narrow = narrow;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (_narrow)
        {
            Body.ColumnDefinitions = ColumnDefinitions.Parse("Auto");
            Body.RowDefinitions = RowDefinitions.Parse("Auto,Auto");
            Grid.SetColumn(Spread, 0);
            Grid.SetRow(Spread, 0);
            Grid.SetColumn(List, 0);
            Grid.SetRow(List, 1);
            Spread.HorizontalAlignment = HorizontalAlignment.Center;
            List.HorizontalAlignment = HorizontalAlignment.Center;
            List.Margin = new Thickness(0, NarrowListGap, 0, 0);
            List.TwoColumns = true;
            return;
        }

        Body.ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto");
        Body.RowDefinitions = RowDefinitions.Parse("Auto");
        Grid.SetColumn(Spread, 0);
        Grid.SetRow(Spread, 0);
        Grid.SetColumn(List, 1);
        Grid.SetRow(List, 0);
        Spread.HorizontalAlignment = HorizontalAlignment.Left;
        List.HorizontalAlignment = HorizontalAlignment.Left;
        List.Margin = new Thickness(InlineListGap, 0, 0, 0);
        List.TwoColumns = false;
    }

    private void OnOpenWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_spread is not { } spread)
            return;
        // 同一個附件（CallId）重複開啟只帶到前面；沒有 ViewModel 時用牌陣物件的身分當 key
        var key = (DataContext as TarotAttachmentViewModel)?.CallId
            ?? $"spread-{RuntimeHelpers.GetHashCode(spread)}";
        TarotSpreadWindowRegistry.Default.Open(key, spread, ConversationTitle());
        OpenWindowRequested?.Invoke(this, spread);
    }

    /// <summary>從視覺樹往上找 DataContext 是工作台 ViewModel 的節點，取它的標題（對話標題）；找不到回 null。</summary>
    private string? ConversationTitle() =>
        this.GetVisualAncestors().OfType<StyledElement>()
            .Select(element => element.DataContext)
            .OfType<WorkbenchViewModel>()
            .FirstOrDefault()?.Title;
}
