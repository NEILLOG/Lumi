using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Views;
using Lumi.App.Views.Tarot;
using Lumi.Tarot;

namespace Lumi.App.Tests.Headless;

/// <summary>M25 25G：點牌放大（合成的牌面＋說明區）與接線。</summary>
public class TarotCardViewerTests
{
    private sealed class NoImages : ITarotCardImages
    {
        public Stream? OpenCard(string cardId) => null;

        public Stream? OpenFrame() => null;

        public TarotWindow? FrameWindow => null;
    }

    private static TarotPlacedCard Chariot(bool reversed = true) =>
        new(1, "指引", "major_07", "戰車", "The Chariot", null, reversed, 0, 0, 0, 0)
        {
            PositionMeaning = "此刻最值得留意的主題",
            Keywords = ["失控", "方向分歧"],
            Meaning = "先停下來確認要去的地方。",
        };

    private static List<string?> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    [AvaloniaFact]
    public void Viewer_shows_the_card_face_with_title_and_details()
    {
        var window = TarotCardViewer.Create(Chariot(), new NoImages());
        window.Show();
        window.UpdateLayout();

        Assert.Equal("指引・戰車（逆位）", window.Title);
        var face = Assert.Single(window.GetVisualDescendants().OfType<TarotCardFace>());
        Assert.Equal(700, face.Width);
        Assert.Equal(1200, face.Height);

        var details = Assert.Single(window.GetVisualDescendants().OfType<TarotCardDetailsView>());
        var texts = Texts(details);
        Assert.Contains("指引", texts);
        Assert.Contains("此刻最值得留意的主題", texts);
        Assert.Contains("戰車", texts);
        Assert.Contains("The Chariot・大阿爾克那 VII", texts);
        Assert.Contains(Strings.Tarot_Reversed, texts);
        Assert.Contains("失控", texts);
        Assert.Contains("方向分歧", texts);
        Assert.Contains("先停下來確認要去的地方。", texts);
        window.Close();
    }

    [AvaloniaFact]
    public void Details_hide_the_rows_with_no_data()
    {
        var details = new TarotCardDetailsView(new TarotPlacedCard(1, "過去", "cups_13", "聖杯國王", "King of Cups", "水", false, 0, 0, 0, 0));
        var window = new Window { Content = details };
        window.Show();
        window.UpdateLayout();

        Assert.False(details.FindControl<ItemsControl>("KeywordList")!.IsVisible);
        Assert.False(details.FindControl<TextBlock>("MeaningText")!.IsVisible);
        Assert.Equal(Strings.Tarot_Upright, details.FindControl<TextBlock>("OrientationText")!.Text);
        window.Close();
    }

    [Theory]
    [InlineData("major_07", "The Chariot", "The Chariot・大阿爾克那 VII")]
    [InlineData("cups_02", "Two of Cups", "Two of Cups・小阿爾克那 2")]
    [InlineData("cups_13", "King of Cups", "King of Cups・小阿爾克那")]
    public void Subtitle_has_english_name_and_number(string id, string english, string expected)
    {
        var card = new TarotPlacedCard(1, "位置", id, "牌", english, null, false, 0, 0, 0, 0);

        Assert.Equal(expected, TarotText.Subtitle(card));
    }

    [AvaloniaFact]
    public void Zoom_buttons_work_on_the_card_face()
    {
        var window = TarotCardViewer.Create(Chariot(), new NoImages());
        window.Show();
        window.UpdateLayout();
        var zoom = window.FindControl<TextBlock>("ZoomText")!;

        window.FindControl<Button>("ActualButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("100%", zoom.Text);
        window.FindControl<Button>("ZoomInButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("125%", zoom.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Clicking_a_card_in_the_spread_window_opens_it_owned_by_that_window()
    {
        var card = Chariot();
        var owner = new Window();
        owner.Show();
        var source = new Border();
        owner.Content = source;
        owner.UpdateLayout();

        TarotCardViewer.Open(card, source, new NoImages());

        var viewer = Assert.IsType<ImageViewerWindow>(Assert.Single(owner.OwnedWindows));
        Assert.Equal("指引・戰車（逆位）", viewer.Title);
        viewer.Close();
        owner.Close();
    }

    [AvaloniaFact]
    public void Viewer_details_row_is_hidden_for_plain_images()
    {
        var window = new ImageViewerWindow((string?)null);
        window.Show();

        Assert.False(window.FindControl<ContentControl>("DetailsHost")!.IsVisible);
        window.Close();
    }
}
