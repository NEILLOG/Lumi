using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Tarot;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M25 25E：回覆附件的排版（ui-spec 3.1）：圖片連續時寬度夠就並排（頂端對齊，間距 12），放不下換行；
/// 牌陣附件永遠自成一列。Wide 寬 776 時兩張 320 並排（320＋12＋320 ＝ 652）。
/// </summary>
public class ReplyAttachmentsPanelTests
{
    private static Border Image(int pixelWidth = 1024, int pixelHeight = 1024)
    {
        var vm = ImageAttachmentViewModel.ForFile("c1", "/nowhere/a.png", exists: false, pixelWidth, pixelHeight);
        return new Border { Width = vm.DisplayWidth, Height = vm.DisplayHeight, DataContext = vm };
    }

    private static Border Tarot() =>
        new() { Width = 291, Height = 398, DataContext = new TarotAttachmentViewModel("c2", new TarotSpreadModel("凱爾特十字", true, [])) };

    /// <summary>量測並排好版，回傳每個子項目的位置（依加入順序）。</summary>
    private static List<Rect> Layout(ReplyAttachmentsPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(new Size(width, panel.DesiredSize.Height)));
        return panel.Children.Select(c => c.Bounds).ToList();
    }

    private static ReplyAttachmentsPanel PanelOf(params Control[] children)
    {
        var panel = new ReplyAttachmentsPanel();
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    [AvaloniaFact]
    public void Wide_776_puts_two_320_images_side_by_side_top_aligned()
    {
        var panel = PanelOf(Image(), Image());

        var bounds = Layout(panel, 776);

        Assert.Equal(new Rect(0, 0, 320, 320), bounds[0]);
        Assert.Equal(new Rect(332, 0, 320, 320), bounds[1]);
    }

    [AvaloniaFact]
    public void Narrow_500_stacks_the_two_images_with_a_12_gap()
    {
        var panel = PanelOf(Image(), Image());

        var bounds = Layout(panel, 500);

        Assert.Equal(0, bounds[0].X);
        Assert.Equal(0, bounds[1].X);
        Assert.Equal(332, bounds[1].Y);
    }

    [AvaloniaFact]
    public void Tarot_spread_is_never_in_the_same_row_as_an_image()
    {
        var panel = PanelOf(Image(), Tarot(), Image());

        var bounds = Layout(panel, 776);

        // 圖片在第一列；牌陣自成一列（左側、在圖片下方 12）；後面的圖片又自成一列
        Assert.Equal(new Rect(0, 0, 320, 320), bounds[0]);
        Assert.Equal(new Rect(0, 332, 291, 398), bounds[1]);
        Assert.Equal(0, bounds[2].X);
        Assert.Equal(742, bounds[2].Y);
    }

    [AvaloniaFact]
    public void Third_image_wraps_to_a_new_row_when_the_first_two_fill_the_width()
    {
        var panel = PanelOf(Image(), Image(), Image());

        var bounds = Layout(panel, 776);

        Assert.Equal(332, bounds[1].X);
        Assert.Equal(0, bounds[2].X);
        Assert.Equal(332, bounds[2].Y);
    }

    [AvaloniaFact]
    public void Portrait_images_pack_side_by_side_by_their_real_width()
    {
        // 1024×1536 → 213×320：三張 213 ＋ 兩個間距 24 ＝ 663，寬 776 放得下三張
        var panel = PanelOf(Image(1024, 1536), Image(1024, 1536), Image(1024, 1536));

        var bounds = Layout(panel, 776);

        Assert.Equal(0, bounds[0].Y);
        Assert.Equal(0, bounds[2].Y);
        Assert.Equal(225, bounds[1].X);
        Assert.Equal(450, bounds[2].X);
        Assert.Equal(320, panel.DesiredSize.Height);
    }

    [AvaloniaFact]
    public void Height_is_the_sum_of_rows_and_gaps()
    {
        var panel = PanelOf(Image(), Tarot(), Image());

        panel.Measure(new Size(776, double.PositiveInfinity));

        Assert.Equal(320 + 12 + 398 + 12 + 320, panel.DesiredSize.Height);
        Assert.Equal(320, panel.DesiredSize.Width);
    }
}
