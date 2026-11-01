using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.App.Views.Tarot;
using Lumi.Tarot;

namespace Lumi.App.Tests.Headless;

/// <summary>25D：塔羅牌陣附件的牌寬、位置名稱、凱爾特十字的編號圓點、對照清單、Narrow 改排、開視窗按鈕與點牌事件。</summary>
public class TarotAttachmentViewTests
{
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

    private static TarotSpreadModel ThreeSpread() => new("三張牌", true,
    [
        Placed(0, "major_00", "愚者", -1, 0),
        Placed(1, "swords_03", "寶劍三", 0, 0, reversed: true),
        Placed(2, "cups_01", "聖杯一", 1, 0),
    ]) { SpreadId = "three_card_timeline" };

    private static TarotSpreadModel SingleCard() => new("單張牌", true,
    [
        Placed(0, "major_00", "愚者", 0, 0),
    ]) { SpreadId = "single_card" };

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

    private static Window Show(Control content, double width = 900, double height = 900)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static List<TarotCardFace> Faces(Control root) => root.GetVisualDescendants().OfType<TarotCardFace>().ToList();

    private static List<Panel> Holders(Control root) => Faces(root).Select(f => (Panel)f.Parent!).ToList();

    private static List<TarotNumberBadge> Badges(Control root) => root.GetVisualDescendants().OfType<TarotNumberBadge>().ToList();

    private static List<TarotCaption> Captions(Control root) => root.GetVisualDescendants().OfType<TarotCaption>().ToList();

    private static string CaptionText(TarotCaption caption) =>
        string.Concat(caption.FindControl<TextBlock>("Text")!.Inlines!.OfType<Run>().Select(run => run.Text));

    private static TarotSpreadView SpreadView(TarotSpreadModel spread, double cardWidth, TarotArrangement arrangement = TarotArrangement.Original, bool numbers = false, bool captions = false) =>
        new()
        {
            Images = new FakeTarotImages(),
            Spread = spread,
            IsActive = true,
            CardWidth = cardWidth,
            Arrangement = arrangement,
            ShowNumbers = numbers,
            ShowCaptions = captions,
        };

    private static TarotAttachmentView Attachment(TarotSpreadModel spread) =>
        new() { DataContext = new TarotAttachmentViewModel("c1", spread) };

    [AvaloniaFact]
    public void Changing_card_width_rebuilds_the_faces_at_the_new_size()
    {
        var view = SpreadView(ThreeSpread(), 56);
        var window = Show(view);
        Assert.Equal(56, Faces(view)[0].Width, 6);

        view.CardWidth = 96;
        window.UpdateLayout();

        var faces = Faces(view);
        Assert.Equal(3, faces.Count);
        Assert.Equal(96, faces[0].Width, 6);
        Assert.Equal(96, Holders(view)[0].Width, 6);
    }

    [AvaloniaFact]
    public void Attachment_uses_card_width_120_96_56_and_72_by_spread_id()
    {
        var cases = new (TarotSpreadModel Spread, double Width)[]
        {
            (SingleCard(), 120),
            (ThreeSpread(), 96),
            (CelticCross(), 56),
            (new TarotSpreadModel("其他", true, [Placed(0, "major_00", "愚者", 0, 0)]), 72),
        };
        foreach (var (spread, width) in cases)
        {
            var attachment = Attachment(spread);
            Show(attachment, 900, 900);

            var view = attachment.FindControl<TarotSpreadView>("Spread")!;
            Assert.Equal(width, view.CardWidth);
            Assert.Equal(width, Faces(view)[0].Width, 6);
        }
    }

    [AvaloniaFact]
    public void Position_captions_show_only_with_show_captions_and_contain_the_reversed_suffix()
    {
        var view = SpreadView(ThreeSpread(), 96, captions: true);
        Show(view);

        var captions = Captions(view);
        Assert.Equal(3, captions.Count);
        Assert.Equal("位置1・寶劍三（逆位）", CaptionText(captions[1]));
        Assert.Equal("位置0・愚者", CaptionText(captions[0]));

        var text = captions[1].FindControl<TextBlock>("Text")!;
        Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.Equal(96 * TarotSpreadLayout.HorizontalGap, captions[1].Width, 6);   // 三張 96 → 139.2
        Assert.Equal("位置1・寶劍三（逆位）", ToolTip.GetTip(captions[1]));

        // 位置名稱在牌下方 8
        var holder = Holders(view)[1];
        Assert.Equal(Canvas.GetTop(holder) + holder.Height + 8, Canvas.GetTop(captions[1]), 6);

        var plain = SpreadView(ThreeSpread(), 96, captions: false);
        Show(plain);
        Assert.Empty(Captions(plain));
    }

    [AvaloniaFact]
    public void Caption_width_limit_is_the_single_card_width_times_the_gap()
    {
        var view = SpreadView(SingleCard(), 120, captions: true);
        Show(view);

        var caption = Captions(view).Single();
        Assert.Equal(120 * TarotSpreadLayout.HorizontalGap, caption.Width, 6);
        Assert.Equal("位置0・愚者", CaptionText(caption));
    }

    [AvaloniaFact]
    public void Celtic_cross_stage_is_about_291_by_398_without_numbers()
    {
        var view = SpreadView(CelticCross(), 56, TarotArrangement.CompactCeltic);
        var window = Show(view);

        var stage = view.FindControl<Canvas>("Stage")!;
        Assert.InRange(stage.Width, 290, 292);
        Assert.InRange(stage.Height, 397, 399);
        Assert.Equal(10, Faces(view).Count);
    }

    [AvaloniaFact]
    public void Numbered_badges_exist_only_for_the_celtic_cross_and_are_numbered_one_to_ten()
    {
        var plain = SpreadView(ThreeSpread(), 96);
        Show(plain);
        Assert.Empty(Badges(plain));

        var celtic = SpreadView(CelticCross(), 56, TarotArrangement.CompactCeltic, numbers: true);
        Show(celtic);
        var badges = Badges(celtic);
        Assert.Equal(10, badges.Count);
        Assert.Equal(Enumerable.Range(1, 10).Select(n => n.ToString()),
            badges.Select(b => b.FindControl<TextBlock>("Number")!.Text));
    }

    [AvaloniaFact]
    public void Badge_sits_six_pixels_outside_the_top_left_corner_of_the_rotated_card()
    {
        var view = SpreadView(CelticCross(), 56, TarotArrangement.CompactCeltic, numbers: true);
        Show(view);
        var holders = Holders(view);
        var badges = Badges(view);

        for (var i = 0; i < holders.Count; i++)
        {
            var holder = holders[i];
            var (boundsWidth, boundsHeight) = TarotSpreadLayout.RotatedBounds(56, CelticCross().Cards[i].Rotation);
            var centerX = Canvas.GetLeft(holder) + holder.Width / 2;
            var centerY = Canvas.GetTop(holder) + holder.Height / 2;
            Assert.Equal(centerX - boundsWidth / 2 - 6, Canvas.GetLeft(badges[i]), 3);
            Assert.Equal(centerY - boundsHeight / 2 - 6, Canvas.GetTop(badges[i]), 3);
        }
    }

    [AvaloniaFact]
    public void Highlighting_only_resizes_the_badge_and_keeps_its_center_and_the_faces()
    {
        var view = SpreadView(CelticCross(), 56, TarotArrangement.CompactCeltic, numbers: true);
        var window = Show(view);
        var facesBefore = Faces(view);
        var badge = Badges(view)[1];
        var centerBefore = Canvas.GetLeft(badge) + badge.Width / 2;
        Assert.Equal(18, badge.Width, 6);

        view.HighlightedIndex = 2;
        window.UpdateLayout();

        var facesAfter = Faces(view);
        Assert.Equal(facesBefore.Count, facesAfter.Count);
        for (var i = 0; i < facesBefore.Count; i++)
            Assert.Same(facesBefore[i], facesAfter[i]);

        Assert.Equal(28, badge.Width, 6);
        Assert.Equal(24, badge.FindControl<Border>("Disc")!.Bounds.Width, 6);
        Assert.Equal(centerBefore, Canvas.GetLeft(badge) + badge.Width / 2, 6);
        Assert.Equal(18, Badges(view)[0].Width, 6);   // 其他圓點不變
    }

    [AvaloniaFact]
    public void Numbered_tooltip_puts_the_number_in_front()
    {
        var view = SpreadView(CelticCross(), 56, TarotArrangement.CompactCeltic, numbers: true);
        Show(view);

        var expected = string.Format(Strings.Tarot_NumberedTooltipFormat, 1,
            string.Format(Strings.Tarot_PositionTooltipFormat, "位置0", "魔術師 EN0", Strings.Tarot_Upright));
        Assert.Equal(expected, ToolTip.GetTip(Holders(view)[0]));
        Assert.Equal(expected, ToolTip.GetTip(Badges(view)[0]));
    }

    [AvaloniaFact]
    public void Position_list_has_ten_rows_in_a_wide_window_to_the_right_of_the_spread()
    {
        var attachment = Attachment(CelticCross());
        var window = Show(attachment, 900, 900);

        var list = attachment.FindControl<TarotPositionList>("List")!;
        var spread = attachment.FindControl<TarotSpreadView>("Spread")!;
        Assert.True(list.IsVisible);
        Assert.Equal(10, list.GetVisualDescendants().OfType<TarotPositionRow>().Count());
        Assert.False(list.TwoColumns);
        Assert.True(list.Bounds.Left > spread.Bounds.Right);
        Assert.Equal(Strings.Tarot_PositionListAutomation, AutomationProperties.GetName(list));
        Assert.Equal("1", list.GetVisualDescendants().OfType<TarotPositionRow>().First().FindControl<TextBlock>("Number")!.Text);
    }

    [AvaloniaFact]
    public void Narrow_attachment_moves_the_list_below_the_spread_in_two_columns()
    {
        var attachment = Attachment(CelticCross());
        var window = Show(attachment, 380, 900);
        window.UpdateLayout();

        var list = attachment.FindControl<TarotPositionList>("List")!;
        var spread = attachment.FindControl<TarotSpreadView>("Spread")!;
        Assert.True(list.TwoColumns);
        Assert.True(list.Bounds.Top >= spread.Bounds.Bottom);
        var columns = list.FindControl<StackPanel>("Rows")!.Children;
        Assert.Equal(2, columns.Count);
        Assert.All(columns, column => Assert.Equal(5, ((StackPanel)column).Children.Count));
        Assert.Equal(10, list.GetVisualDescendants().OfType<TarotPositionRow>().Count());
    }

    [AvaloniaFact]
    public void Open_window_button_exists_only_for_the_celtic_cross_and_raises_the_request()
    {
        var single = Attachment(SingleCard());
        Show(single);
        Assert.False(single.FindControl<Button>("OpenWindowButton")!.IsVisible);

        var celtic = Attachment(CelticCross());
        Show(celtic);
        var button = celtic.FindControl<Button>("OpenWindowButton")!;
        Assert.True(button.IsVisible);

        TarotSpreadModel? requested = null;
        celtic.OpenWindowRequested += (_, spread) => requested = spread;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(requested);
        Assert.Equal("凱爾特十字", requested!.SpreadName);
    }

    [AvaloniaFact]
    public void Clicking_a_card_raises_card_clicked_and_hovering_reports_it()
    {
        var view = SpreadView(ThreeSpread(), 96);
        var window = Show(view);
        var holder = Holders(view)[1];
        var point = holder.TranslatePoint(new Point(holder.Width / 2, holder.Height / 2), window)!.Value;

        TarotPlacedCard? clicked = null;
        view.CardClicked += (_, card) => clicked = card;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.Equal(1, clicked?.Index);

        TarotPlacedCard? hovered = null;
        view.CardHovered += (_, card) => hovered = card;
        window.MouseMove(new Point(880, 880));   // 先離開，免得指標本來就在牌上而不會再進入
        Assert.Null(hovered);
        window.MouseMove(point);
        Assert.Equal(1, hovered?.Index);

        window.MouseMove(new Point(880, 880));
        Assert.Null(hovered);
    }

    [AvaloniaFact]
    public void Cards_show_a_hand_cursor()
    {
        var view = SpreadView(ThreeSpread(), 96);
        Show(view);

        Assert.All(Holders(view), holder => Assert.NotNull(holder.Cursor));
    }

    [AvaloniaFact]
    public void Narrow_window_class_gives_the_card_12_side_padding()
    {
        var attachment = Attachment(ThreeSpread());
        var window = Show(attachment, 900, 900);
        var frame = attachment.FindControl<Border>("Frame")!;
        Assert.Equal(16, frame.Padding.Left);

        window.Classes.Add("narrow");
        window.UpdateLayout();
        Assert.Equal(12, frame.Padding.Left);
        Assert.Equal(16, frame.Padding.Top);
    }
}
