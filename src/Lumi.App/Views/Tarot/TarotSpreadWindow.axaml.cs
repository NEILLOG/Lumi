using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Lumi.App.Resources;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 凱爾特十字牌陣視窗（plan/25 25F；ui-spec 3.1）。獨立彈出視窗，不是 modal，也不設 owner（不綁定工作台視窗；釘選＝Topmost 直接作用在這個視窗上）。
/// 開啟與重複開啟的處理在 <see cref="TarotSpreadWindowRegistry"/>；版面數字在 <see cref="TarotSpreadWindowLayout"/>。
/// </summary>
public partial class TarotSpreadWindow : Window
{
    private readonly TarotSpreadModel _spread;

    /// <param name="spread">要畫的牌陣（凱爾特十字）。</param>
    /// <param name="conversationTitle">對話標題；null 或空白時標題只用牌陣名稱。</param>
    public TarotSpreadWindow(TarotSpreadModel spread, string? conversationTitle)
    {
        _spread = spread;
        InitializeComponent();
        Title = TitleFor(spread.SpreadName, conversationTitle);
        AutomationProperties.SetName(this, Title);

        // 資料原座標，不縮直排間距；有編號圓點、沒有位置名稱（清單負責說明位置）
        Spread.Arrangement = TarotArrangement.Original;
        Spread.ShowNumbers = true;
        Spread.ShowCaptions = false;
        Spread.CardWidth = TarotSpreadWindowLayout.DefaultCardWidth;
        Spread.Spread = spread;
        Spread.CardClicked += (_, card) => TarotCardViewer.Open(card, this, Spread.Images);
        Spread.CardHovered += (_, card) => SetHighlight(card is null ? null : NumberOf(card));

        List.Spread = spread;
        List.RowHovered += (_, number) => SetHighlight(number);

        SpreadHost.SizeChanged += (_, e) => ApplyCardWidth(e.NewSize);
        PinButton.Click += OnPinClick;
        UpdatePin();
    }

    /// <summary>視窗標題：「{牌陣名稱}・{對話標題}」；沒有對話標題時只有牌陣名稱。</summary>
    public static string TitleFor(string spreadName, string? conversationTitle) =>
        string.IsNullOrWhiteSpace(conversationTitle)
            ? spreadName
            : string.Format(Strings.Tarot_SpreadWindowTitleFormat, spreadName, conversationTitle);

    /// <summary>目前的牌寬（測試與版面檢查用）。</summary>
    internal double CurrentCardWidth => Spread.CardWidth;

    /// <summary>目前高亮的編號（1～10）；null 表示沒有。</summary>
    internal int? HighlightedIndex => Spread.HighlightedIndex;

    /// <summary>Topmost 改變（按鈕或其他途徑）時同步釘選按鈕的樣式。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TopmostProperty)
            UpdatePin();
    }

    private void OnPinClick(object? sender, RoutedEventArgs e) => Topmost = !Topmost;

    /// <summary>釘選按鈕的樣式與提示跟著 Topmost 走。</summary>
    private void UpdatePin() => PinButton.Classes.Set("pinned", Topmost);

    /// <summary>牌寬隨牌陣區的可用寬高等比縮放；變化不到 <see cref="TarotSpreadWindowLayout.RebuildThreshold"/> 就不重建。</summary>
    private void ApplyCardWidth(Size hostSize)
    {
        if (hostSize.Width <= 0 || hostSize.Height <= 0)
            return;   // 最小化或還沒排版：不要把牌寬縮到下限
        var padding = SpreadHost.Padding;
        var next = TarotSpreadWindowLayout.CardWidthFor(
            _spread,
            hostSize.Width - padding.Left - padding.Right,
            hostSize.Height - padding.Top - padding.Bottom);
        if (TarotSpreadWindowLayout.ShouldUpdate(Spread.CardWidth, next))
            Spread.CardWidth = next;
    }

    /// <summary>牌陣上的編號（1 起算，對應清單的列）；找不到時 null。</summary>
    private int? NumberOf(TarotPlacedCard card)
    {
        for (var i = 0; i < _spread.Cards.Count; i++)
        {
            if (ReferenceEquals(_spread.Cards[i], card))
                return i + 1;
        }
        return null;
    }

    /// <summary>清單與牌互相標示：同一個編號同時高亮（圓點與清單行）；null 時全部清掉。</summary>
    private void SetHighlight(int? number)
    {
        Spread.HighlightedIndex = number;
        List.HighlightedIndex = number;
    }
}
