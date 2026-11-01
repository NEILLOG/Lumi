using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Views.Tarot;
using Lumi.Tarot;

namespace Lumi.App.Tests.Headless;

/// <summary>25F：凱爾特十字牌陣視窗（<see cref="TarotSpreadWindow"/>）的標題、釘選、版面、互相標示與重複開啟。</summary>
public class TarotSpreadWindowTests
{
    /// <summary>可控的假圖片來源（不讀內嵌素材，測試快）。</summary>
    private sealed class FakeTarotImages : ITarotCardImages
    {
        public Stream? OpenCard(string cardId) => PngStream();

        public Stream? OpenFrame() => PngStream();

        public TarotWindow? FrameWindow => null;
    }

    /// <summary>1×1 的合法 PNG。</summary>
    private static Stream PngStream() => new MemoryStream(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));

    private static TarotPlacedCard Placed(int index, string cardId, string name, double x, double y, double rotation = 0, int z = 0, bool reversed = false) =>
        new(index, "位置" + index, cardId, name, "EN" + index, "風", reversed, x, y, rotation, z);

    /// <summary>凱爾特十字，座標照 plan/25 「牌陣的實際版面」（交叉牌橫放 90° 且 z=1）。</summary>
    private static TarotSpreadModel CelticCross() => new("凱爾特十字", true,
    [
        Placed(0, "major_01", "魔術師", 0, 0),
        Placed(1, "major_02", "女祭司", 0, 0, rotation: 90, z: 1),
        Placed(2, "major_03", "皇后", 0, -1),
        Placed(3, "major_04", "皇帝", 0, 1),
        Placed(4, "major_05", "教皇", 1, 0),
        Placed(5, "major_06", "戀人", -1, 0),
        Placed(6, "major_07", "戰車", 3, 1.5),
        Placed(7, "major_08", "力量", 3, 0.5),
        Placed(8, "major_09", "隱士", 3, -0.5),
        Placed(9, "major_10", "命運之輪", 3, -1.5),
    ]) { SpreadId = "celtic_cross" };

    /// <summary>建立視窗並換成假圖片；還沒 Show。</summary>
    private static TarotSpreadWindow Make(string? conversationTitle = "測試對話")
    {
        var window = new TarotSpreadWindow(CelticCross(), conversationTitle);
        window.FindControl<TarotSpreadView>("Spread")!.Images = new FakeTarotImages();
        return window;
    }

    private static TarotSpreadView SpreadOf(Window window) => window.FindControl<TarotSpreadView>("Spread")!;

    private static TarotPositionList ListOf(Window window) => window.FindControl<TarotPositionList>("List")!;

    private static Button PinOf(Window window) => window.FindControl<Button>("PinButton")!;

    /// <summary>牌陣的各張牌（外層的 Panel，有位置與旋轉）。</summary>
    private static List<Control> Holders(TarotSpreadView spread) =>
        spread.GetVisualDescendants().OfType<TarotCardFace>().Select(face => (Control)face.Parent!).ToList();

    private static List<TarotPositionRow> Rows(TarotPositionList list) =>
        list.GetVisualDescendants().OfType<TarotPositionRow>().OrderBy(row => row.PositionNumber).ToList();

    private static Point CenterOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    [AvaloniaFact]
    public void Title_contains_spread_name_and_conversation_title()
    {
        var window = Make("測試對話");
        Assert.Equal(string.Format(Strings.Tarot_SpreadWindowTitleFormat, "凱爾特十字", "測試對話"), window.Title);
        Assert.Equal(string.Format(Strings.Tarot_SpreadWindowTitleFormat, "凱爾特十字", "測試對話"),
            TarotSpreadWindow.TitleFor("凱爾特十字", "測試對話"));
    }

    [AvaloniaFact]
    public void Title_without_conversation_title_is_the_spread_name_only()
    {
        Assert.Equal("凱爾特十字", TarotSpreadWindow.TitleFor("凱爾特十字", null));
        Assert.Equal("凱爾特十字", TarotSpreadWindow.TitleFor("凱爾特十字", "  "));
        var window = Make(conversationTitle: null);
        Assert.Equal("凱爾特十字", window.Title);
    }

    [AvaloniaFact]
    public void Default_size_and_resizable()
    {
        var window = Make();
        Assert.Equal(600, window.Width);
        Assert.Equal(760, window.Height);
        Assert.Equal(360, window.MinWidth);
        Assert.Equal(480, window.MinHeight);
        Assert.True(window.CanResize);
    }

    [AvaloniaFact]
    public void Pin_button_toggles_topmost_and_its_style_class()
    {
        var window = Make();
        window.Show();
        window.UpdateLayout();
        var pin = PinOf(window);

        Assert.Equal(Strings.Tarot_KeepOnTop, ToolTip.GetTip(pin));
        Assert.Equal(Strings.Tarot_KeepOnTop, AutomationProperties.GetName(pin));
        Assert.False(window.Topmost);
        Assert.DoesNotContain("pinned", pin.Classes);

        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(window.Topmost);
        Assert.Contains("pinned", pin.Classes);

        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(window.Topmost);
        Assert.DoesNotContain("pinned", pin.Classes);
    }

    [AvaloniaFact]
    public void Spread_uses_original_arrangement_with_numbers_and_no_captions()
    {
        var window = Make();
        window.Show();
        window.UpdateLayout();
        var spread = SpreadOf(window);

        Assert.Equal(TarotArrangement.Original, spread.Arrangement);
        Assert.True(spread.ShowNumbers);
        Assert.False(spread.ShowCaptions);
        Assert.Equal(10, spread.GetVisualDescendants().OfType<TarotNumberBadge>().Count());
        Assert.Empty(spread.GetVisualDescendants().OfType<TarotCaption>());
    }

    [AvaloniaFact]
    public void Original_arrangement_keeps_data_x_for_the_right_column()
    {
        var window = Make();
        window.Show();
        window.UpdateLayout();
        var spread = SpreadOf(window);
        var holders = Holders(spread);

        // 教皇 x=1、戰車 x=3：原座標下中心相差 2 × 橫向間距 × 牌寬（縮小版會把直排靠近，這裡不會）
        var distance = Canvas.GetLeft(holders[6]) - Canvas.GetLeft(holders[4]);
        Assert.Equal(2 * TarotSpreadLayout.HorizontalGap * spread.CardWidth, distance, 3);
    }

    [AvaloniaFact]
    public void Card_width_follows_window_size_and_stays_within_limits()
    {
        var window = Make();
        window.Show();
        window.UpdateLayout();
        var spread = SpreadOf(window);

        // 預設視窗：算出的牌寬約 72（實際 ≈ 71.96，與 72 的差在 1 以內，所以不重建）
        Assert.InRange(spread.CardWidth, 71, 73);

        window.Width = 900;
        window.Height = 1000;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(spread.CardWidth > 80, $"放大後牌寬應變大，實際 {spread.CardWidth}，host {window.FindControl<Border>("SpreadHost")!.Bounds}，client {window.ClientSize}");

        window.Width = 360;
        window.Height = 480;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.True(spread.CardWidth < 60, $"縮小後牌寬應變小，實際 {spread.CardWidth}");
        Assert.True(spread.CardWidth >= TarotSpreadWindowLayout.MinCardWidth);
    }

    [AvaloniaFact]
    public void Position_list_has_two_columns_of_ten_rows()
    {
        var window = Make();
        window.Show();
        window.UpdateLayout();
        var list = ListOf(window);

        Assert.True(list.TwoColumns);
        Assert.Equal(2, list.FindControl<StackPanel>("Rows")!.Children.Count);
        var rows = Rows(list);
        Assert.Equal(10, rows.Count);
        Assert.Equal(Enumerable.Range(1, 10), rows.Select(row => row.PositionNumber));
        Assert.All(rows, row => Assert.True(row.Interactive));
    }

    [AvaloniaFact]
    public void Hovering_a_list_row_highlights_the_dot_and_the_row_and_leaving_clears_both()
    {
        var window = Make();
        window.Show();
        window.UpdateLayout();
        var rows = Rows(ListOf(window));
        var row = rows[2];

        window.MouseMove(CenterOf(row, window));
        window.UpdateLayout();
        Assert.Equal(3, window.HighlightedIndex);
        Assert.Equal(3, SpreadOf(window).HighlightedIndex);
        Assert.True(row.Highlighted);
        Assert.Equal(1, rows.Count(r => r.Highlighted));

        window.MouseMove(new Point(2, 2));   // 視窗左上角的空白，不在清單上
        window.UpdateLayout();
        Assert.Null(window.HighlightedIndex);
        Assert.Null(SpreadOf(window).HighlightedIndex);
        Assert.False(row.Highlighted);
    }

    [AvaloniaFact]
    public void Hovering_a_card_highlights_its_row_and_leaving_clears_it()
    {
        var window = Make();
        window.Show();
        window.UpdateLayout();
        var spread = SpreadOf(window);
        var holder = Holders(spread)[6];   // 第 7 張：戰車（右側直排，不與其他牌重疊）

        window.MouseMove(CenterOf(holder, window));
        window.UpdateLayout();
        Assert.Equal(7, window.HighlightedIndex);
        Assert.Equal(7, spread.HighlightedIndex);
        Assert.True(Rows(ListOf(window))[6].Highlighted);

        window.MouseMove(new Point(2, 2));
        window.UpdateLayout();
        Assert.Null(window.HighlightedIndex);
        Assert.Null(spread.HighlightedIndex);
        Assert.False(Rows(ListOf(window))[6].Highlighted);
    }

    [AvaloniaFact]
    public void Conversation_sized_list_is_unchanged_by_the_window_additions()
    {
        // 對話內縮小版：不互動、沒有內距、沒有高亮（外觀與 25D 相同）
        var list = new TarotPositionList { Spread = CelticCross() };
        var window = new Window { Width = 600, Height = 400, Content = list };
        window.Show();
        window.UpdateLayout();
        var row = Rows(list)[0];

        Assert.False(row.Interactive);
        Assert.Equal(default, row.FindControl<Border>("Root")!.Padding);
        Assert.False(list.TwoColumns);
        window.Close();
    }

    [AvaloniaFact]
    public void Reopening_the_same_attachment_brings_the_existing_window_forward()
    {
        var registry = new TarotSpreadWindowRegistry();
        var spread = CelticCross();

        var (first, created) = registry.Open("call-1", spread, "測試對話");
        var (second, createdAgain) = registry.Open("call-1", spread, "測試對話");
        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Same(first, second);
        Assert.Equal(1, registry.Count);

        var (other, otherCreated) = registry.Open("call-2", spread, "測試對話");
        Assert.True(otherCreated);
        Assert.NotSame(first, other);
        Assert.Equal(2, registry.Count);

        first.Close();
        Assert.Equal(1, registry.Count);
        other.Close();
        Assert.Equal(0, registry.Count);
    }
}
