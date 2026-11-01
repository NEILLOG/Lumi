using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.App.Views.Tarot;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tarot;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>25B：工具卡片的牌陣圖（<see cref="TarotSpreadView"/>、<see cref="TarotCardFace"/>）實際渲染出來的結構。</summary>
public class TarotSpreadViewTests
{
    /// <summary>可控的假圖片來源；計數 OpenFrame 次數，用來確認邊框只解一次。</summary>
    private sealed class FakeTarotImages : ITarotCardImages
    {
        public Func<string, Stream?> Card { get; init; } = _ => null;

        public Func<Stream?> Frame { get; init; } = () => null;

        public TarotWindow? FrameWindowValue { get; init; }

        public int FrameOpens { get; private set; }

        public Stream? OpenCard(string cardId) => Card(cardId);

        public Stream? OpenFrame()
        {
            FrameOpens++;
            return Frame();
        }

        public TarotWindow? FrameWindow => FrameWindowValue;
    }

    /// <summary>1×1 的合法 PNG。</summary>
    private static Stream PngStream() => new MemoryStream(Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));

    private static Stream BrokenStream() => new MemoryStream([1, 2, 3]);

    private static TarotPlacedCard Placed(int index, string cardId, string name, double x, double y, double rotation = 0, int z = 0, bool reversed = false) =>
        new(index, "位置" + index, cardId, name, "EN" + index, "風", reversed, x, y, rotation, z);

    private static TarotSpreadModel ThreeSpread() => new("三張牌", true,
    [
        Placed(0, "major_00", "愚者", -1, 0),
        Placed(1, "swords_03", "寶劍三", 0, 0, reversed: true),
        Placed(2, "cups_01", "聖杯一", 1, 0),
    ]);

    /// <summary>凱爾特十字，座標照 plan/25 「牌陣的實際版面」（x 牌寬、y 牌高，交叉牌橫放 90° 且 z=1）。</summary>
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
    ]);

    private static (TarotSpreadView View, Window Window) Show(TarotSpreadView view)
    {
        var window = new Window { Width = 800, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static List<TarotCardFace> Faces(Control root) => root.GetVisualDescendants().OfType<TarotCardFace>().ToList();

    [AvaloniaFact]
    public void Three_cards_render_three_faces_with_tooltips_and_a_name()
    {
        var images = new FakeTarotImages { Card = _ => PngStream(), Frame = PngStream };
        var (view, _) = Show(new TarotSpreadView { Images = images, Spread = ThreeSpread(), IsActive = true });

        var faces = Faces(view);
        Assert.Equal(3, faces.Count);
        Assert.Equal(1, images.FrameOpens);   // 邊框整個牌陣只解一次
        Assert.Equal(string.Format(Strings.Tarot_SpreadAutomationFormat, "三張牌"), AutomationProperties.GetName(view));
        Assert.Equal(string.Format(Strings.Tarot_PositionTooltipFormat, "位置1", "寶劍三 EN1", Strings.Tarot_Reversed),
            ToolTip.GetTip((Control)faces[1].Parent!));
        Assert.Equal("愚者", faces[0].FindControl<TextBlock>("NameZh")!.Text);
    }

    [AvaloniaFact]
    public void Collapsed_view_builds_nothing_and_decodes_nothing()
    {
        var images = new FakeTarotImages { Card = _ => PngStream(), Frame = PngStream };
        var (view, _) = Show(new TarotSpreadView { Images = images, Spread = ThreeSpread(), IsActive = false });

        Assert.Empty(Faces(view));
        Assert.Equal(0, images.FrameOpens);

        view.IsActive = true;
        Assert.Equal(3, Faces(view).Count);

        view.IsActive = false;   // 收合：釋放並清空
        Assert.Empty(Faces(view));
    }

    [AvaloniaFact]
    public void Detaching_clears_the_faces_and_reattaching_loads_them_again()
    {
        var images = new FakeTarotImages { Card = _ => PngStream(), Frame = PngStream };
        var view = new TarotSpreadView { Images = images, Spread = ThreeSpread(), IsActive = true };
        var (_, window) = Show(view);
        Assert.Equal(3, Faces(view).Count);

        window.Content = null;   // 從視覺樹拿掉
        Assert.Empty(Faces(view));

        window.Content = view;
        window.UpdateLayout();
        Assert.Equal(3, Faces(view).Count);
        Assert.Equal(2, images.FrameOpens);
    }

    [AvaloniaFact]
    public void Reversed_card_rotates_only_the_illustration_layer_around_the_window_center()
    {
        var images = new FakeTarotImages { Card = _ => PngStream(), Frame = PngStream };
        var (view, _) = Show(new TarotSpreadView { Images = images, Spread = ThreeSpread(), IsActive = true });
        var reversed = Faces(view)[1];

        var illustration = reversed.FindControl<Panel>("IllustrationLayer")!;
        var rotate = Assert.IsType<RotateTransform>(illustration.RenderTransform);
        Assert.Equal(180, rotate.Angle, 6);
        Assert.Equal(0.44, illustration.RenderTransformOrigin.Point.Y, 6);   // 預設窗口中心 0.05 + 0.78 / 2

        Assert.Null(reversed.FindControl<Panel>("FrameLayer")!.RenderTransform);
        Assert.Null(reversed.FindControl<Border>("BaseLayer")!.RenderTransform);
        Assert.Null(reversed.FindControl<Canvas>("TextLayer")!.RenderTransform);
        Assert.Null(reversed.RenderTransform);   // 牌面本身不轉；位置的旋轉在外層

        var upright = Faces(view)[0];
        Assert.Null(upright.FindControl<Panel>("IllustrationLayer")!.RenderTransform);
        Assert.False(upright.FindControl<Border>("ReversedBadge")!.IsVisible);
        Assert.True(reversed.FindControl<Border>("ReversedBadge")!.IsVisible);
    }

    [AvaloniaFact]
    public void Custom_frame_window_moves_the_rotation_center()
    {
        var images = new FakeTarotImages
        {
            Card = _ => PngStream(),
            Frame = PngStream,
            FrameWindowValue = new TarotWindow(0.1, 0.2, 0.8, 0.6),
        };
        var (view, _) = Show(new TarotSpreadView { Images = images, Spread = ThreeSpread(), IsActive = true });

        var illustration = Faces(view)[1].FindControl<Panel>("IllustrationLayer")!;
        Assert.Equal(0.5, illustration.RenderTransformOrigin.Point.Y, 6);
    }

    [AvaloniaFact]
    public void Frame_image_replaces_the_gold_frame_and_its_absence_shows_the_gold_frame()
    {
        var withFrame = new FakeTarotImages { Card = _ => PngStream(), Frame = PngStream };
        var (framed, _) = Show(new TarotSpreadView { Images = withFrame, Spread = ThreeSpread(), IsActive = true });
        var face = Faces(framed)[0];
        Assert.True(face.FindControl<Image>("FrameImage")!.IsVisible);
        Assert.False(face.FindControl<Border>("GoldFrame")!.IsVisible);

        var noFrame = new FakeTarotImages { Card = _ => PngStream(), Frame = () => null };
        var (plain, _) = Show(new TarotSpreadView { Images = noFrame, Spread = ThreeSpread(), IsActive = true });
        var plainFace = Faces(plain)[0];
        Assert.False(plainFace.FindControl<Image>("FrameImage")!.IsVisible);
        Assert.True(plainFace.FindControl<Border>("GoldFrame")!.IsVisible);
    }

    [AvaloniaFact]
    public void Without_any_assets_suit_blocks_and_names_are_still_drawn()
    {
        var images = new FakeTarotImages();
        var (view, _) = Show(new TarotSpreadView { Images = images, Spread = ThreeSpread(), IsActive = true });

        var faces = Faces(view);
        Assert.Equal(3, faces.Count);
        Assert.False(faces[0].FindControl<Image>("IllustrationImage")!.IsVisible);
        Assert.True(faces[0].FindControl<Border>("SuitBlock")!.IsVisible);
        Assert.Equal("愚者", faces[0].FindControl<TextBlock>("NameZh")!.Text);
        Assert.Contains("EN0", (string)ToolTip.GetTip((Control)faces[0].Parent!)!);   // 英文名放在提示裡
        Assert.Equal("0", faces[0].FindControl<TextBlock>("SealText")!.Text);
        Assert.Equal("寶劍三", faces[1].FindControl<TextBlock>("NameZh")!.Text);
    }

    [AvaloniaFact]
    public void Broken_images_fall_back_without_throwing_and_keep_the_other_cards()
    {
        var images = new FakeTarotImages
        {
            Card = id => id == "major_00" ? BrokenStream() : PngStream(),
            Frame = BrokenStream,
        };
        var (view, _) = Show(new TarotSpreadView { Images = images, Spread = ThreeSpread(), IsActive = true });

        var faces = Faces(view);
        Assert.Equal(3, faces.Count);
        Assert.False(faces[0].FindControl<Image>("IllustrationImage")!.IsVisible);   // 壞掉的插畫：色塊
        Assert.True(faces[0].FindControl<Border>("SuitBlock")!.IsVisible);
        Assert.True(faces[2].FindControl<Image>("IllustrationImage")!.IsVisible);   // 其他張照常
        Assert.True(faces[0].FindControl<Border>("GoldFrame")!.IsVisible);          // 壞掉的邊框：金框
    }

    [AvaloniaFact]
    public void Higher_z_card_is_drawn_on_top()
    {
        var spread = new TarotSpreadModel("測試", true,
        [
            Placed(0, "major_00", "愚者", 0, 0, z: 2),
            Placed(1, "major_01", "魔術師", 0, 0, z: 0),
        ]);
        var (view, _) = Show(new TarotSpreadView { Images = new FakeTarotImages(), Spread = spread, IsActive = true });

        var faces = Faces(view);
        var high = (Panel)faces[0].Parent!;
        var low = (Panel)faces[1].Parent!;
        Assert.True(high.ZIndex > low.ZIndex);
    }

    [AvaloniaFact]
    public void Celtic_cross_has_ten_faces_a_rotated_cross_and_no_overlap_within_a_layer()
    {
        var (view, _) = Show(new TarotSpreadView { Images = new FakeTarotImages(), Spread = CelticCross(), IsActive = true });

        var faces = Faces(view);
        Assert.Equal(10, faces.Count);

        var holders = faces.Select(f => (Panel)f.Parent!).ToList();
        var cross = holders[1];
        Assert.Equal(90, Assert.IsType<RotateTransform>(cross.RenderTransform).Angle, 6);

        // 同一層（z 相同）的牌外框不重疊（D9：HorizontalGap 生效）
        var rects = holders.Select(h =>
        {
            var angle = (h.RenderTransform as RotateTransform)?.Angle ?? 0;
            var radians = angle * Math.PI / 180;
            var w = h.Width * Math.Abs(Math.Cos(radians)) + h.Height * Math.Abs(Math.Sin(radians));
            var hh = h.Width * Math.Abs(Math.Sin(radians)) + h.Height * Math.Abs(Math.Cos(radians));
            var cx = Canvas.GetLeft(h) + h.Width / 2;
            var cy = Canvas.GetTop(h) + h.Height / 2;
            return (Z: h.ZIndex, Left: cx - w / 2, Right: cx + w / 2, Top: cy - hh / 2, Bottom: cy + hh / 2);
        }).ToList();
        for (var i = 0; i < rects.Count; i++)
        for (var j = i + 1; j < rects.Count; j++)
        {
            if (rects[i].Z != rects[j].Z)
                continue;
            var overlaps = rects[i].Left < rects[j].Right && rects[j].Left < rects[i].Right
                && rects[i].Top < rects[j].Bottom && rects[j].Top < rects[i].Bottom;
            Assert.False(overlaps, $"第 {i} 與第 {j} 張重疊");
        }
    }

    [AvaloniaFact]
    public void Empty_spread_renders_nothing_and_does_not_throw()
    {
        var (view, _) = Show(new TarotSpreadView { Images = new FakeTarotImages(), Spread = new TarotSpreadModel("空", true, []), IsActive = true });

        Assert.Empty(Faces(view));
    }

    [AvaloniaFact]
    public void Tool_card_no_longer_draws_the_spread_it_becomes_a_reply_attachment()
    {
        var card = new ToolCardViewModel("c1", "tarot_draw", "塔羅抽牌", ToolPermission.ReadOnly, "三張牌", "{}",
            new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());
        card.Complete(new ToolResultBlock("c1", "找不到牌陣", true, "塔羅抽牌", null, "失敗", 3, ToolApproval.NotRequired));
        card.TarotSpread = ThreeSpread();   // 直接給牌陣，不經過解析器
        var view = new ToolCardView { DataContext = card };
        var window = new Window { Width = 800, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();

        // 牌陣在回覆附件裡（ui-spec 3.1），工具卡片只是過程紀錄：收合、展開都不畫牌
        Assert.Empty(Faces(view));
        card.IsExpanded = true;
        view.UpdateLayout();
        Assert.Empty(Faces(view));
    }

    [AvaloniaFact]
    public void Reply_attachments_view_draws_the_tarot_attachment()
    {
        var message = MessageViewModel.AttachmentHost();
        message.ReplyAttachments.Add(new TarotAttachmentViewModel("c1", ThreeSpread()));
        var view = new ReplyAttachmentsView { DataContext = message };
        var window = new Window { Width = 800, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(3, Faces(view).Count);
    }

    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }
}
