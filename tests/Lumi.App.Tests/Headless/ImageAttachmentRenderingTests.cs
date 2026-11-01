using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tarot;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// M25 25E：圖片附件的三種畫面（完成、產生中、遺失）與回覆附件的並排排版，實際渲染出來的結構與尺寸。
/// </summary>
public class ImageAttachmentRenderingTests
{
    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    /// <summary>1×1 的 PNG，讓縮圖真的能解碼。</summary>
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private static (ImageAttachmentView View, Window Window) Show(ImageAttachmentViewModel vm)
    {
        var view = new ImageAttachmentView { DataContext = vm };
        var window = new Window { Width = 700, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static IEnumerable<Button> VisibleButtons(Visual root) =>
        root.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible);

    [AvaloniaFact]
    public void Pending_placeholder_is_sized_dashed_and_announces_its_status()
    {
        var (view, _) = Show(ImageAttachmentViewModel.Pending("c1", 1536, 1024, DateTimeOffset.UtcNow));

        var placeholder = view.GetVisualDescendants().OfType<Grid>().Single();
        Assert.True(placeholder.IsEffectivelyVisible);
        Assert.Equal(320, placeholder.Width);
        Assert.Equal(213, placeholder.Height);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(placeholder));
        Assert.Equal(Strings.Image_Generating, AutomationProperties.GetName(placeholder));

        var dashed = view.GetVisualDescendants().OfType<Rectangle>().Single();
        Assert.NotNull(dashed.StrokeDashArray);
        Assert.NotEmpty(dashed.StrokeDashArray!);

        var progress = view.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert.True(progress.IsIndeterminate);
        Assert.Equal(120, progress.Width);
        Assert.Equal(4, progress.Height);
        Assert.Equal(4, progress.MinHeight);
    }

    [AvaloniaFact]
    public void Pending_placeholder_has_no_buttons()
    {
        var (view, _) = Show(ImageAttachmentViewModel.Pending("c1", 1024, 1024));

        Assert.Empty(VisibleButtons(view));
    }

    [AvaloniaFact]
    public void Missing_placeholder_shows_the_file_name_and_no_buttons()
    {
        var (view, _) = Show(ImageAttachmentViewModel.ForFile("c1", "/nowhere/20261101-231205.png", exists: false, 1024, 1024));

        var border = view.GetVisualDescendants().OfType<Border>().First(b => b.BorderThickness.Left == 1 && b.IsEffectivelyVisible);
        Assert.Equal(320, border.Width);
        Assert.Equal(320, border.Height);
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(Strings.ToolCard_ImageMissing, texts);
        Assert.Contains("20261101-231205.png", texts);
        Assert.Empty(VisibleButtons(view));
        Assert.False(view.GetVisualDescendants().OfType<Grid>().Single().IsVisible);
    }

    [AvaloniaFact]
    public void Finished_image_has_a_thumbnail_named_by_file_and_three_actions()
    {
        var path = Path.Combine(Path.GetTempPath(), "lumi-25e-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPng));
        try
        {
            var (view, _) = Show(ImageAttachmentViewModel.ForFile("c1", path, exists: true, 1536, 1024));

            var thumbnail = view.FindByName<Button>("Thumbnail");
            Assert.True(thumbnail.IsEffectivelyVisible);
            Assert.Equal(Path.GetFileName(path), ToolTip.GetTip(thumbnail));
            Assert.Equal(string.Format(Strings.Image_AttachmentAutomationFormat, Path.GetFileName(path)), AutomationProperties.GetName(thumbnail));
            // 縮圖依圖片實際比例縮進 320 × 320（不套參數尺寸的框）
            var image = thumbnail.GetVisualDescendants().OfType<Image>().Single();
            Assert.Equal(320, image.MaxWidth);
            Assert.Equal(320, image.MaxHeight);
            Assert.Equal(Stretch.Uniform, image.Stretch);
            Assert.Contains(thumbnail.GetVisualDescendants().OfType<Border>(), b => b.ClipToBounds);

            var actions = VisibleButtons(view).Where(b => b.Classes.Contains("tertiary")).ToList();
            Assert.Equal(3, actions.Count);
            var names = actions.Select(b => AutomationProperties.GetName(b)).ToList();
            Assert.Equal(new[] { Strings.Image_ViewLarger, Strings.Image_OpenExternal, Strings.ToolCard_OpenFolder }, names);
            Assert.False(view.GetVisualDescendants().OfType<Grid>().Single().IsVisible);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Clicking_the_thumbnail_raises_view_larger_with_the_image()
    {
        var path = Path.Combine(Path.GetTempPath(), "lumi-25e-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPng));
        try
        {
            var vm = ImageAttachmentViewModel.ForFile("c1", path, exists: true, 1024, 1024);
            var (view, _) = Show(vm);
            ImageAttachmentViewModel? raised = null;
            view.ViewLargerRequested += (_, image) => raised = image;

            view.FindByName<Button>("Thumbnail").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Same(vm, raised);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Attachments_in_a_wide_message_sit_side_by_side_at_776()
    {
        var message = MessageViewModel.StreamingAssistant();
        var path = Path.Combine(Path.GetTempPath(), "lumi-25e-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Convert.FromBase64String(OnePixelPng));
        try
        {
            message.ReplyAttachments.Add(ImageAttachmentViewModel.ForFile("c1", path, exists: true, 1024, 1024));
            message.ReplyAttachments.Add(ImageAttachmentViewModel.ForFile("c2", path, exists: true, 1024, 1024));
            var view = new ReplyAttachmentsView { DataContext = message };
            var window = new Window { Width = 776, Height = 700, Content = view };
            window.Show();
            window.UpdateLayout();

            var images = view.GetVisualDescendants().OfType<ImageAttachmentView>().ToList();
            Assert.Equal(2, images.Count);
            var first = images[0].TranslatePoint(new Point(0, 0), view)!.Value;
            var second = images[1].TranslatePoint(new Point(0, 0), view)!.Value;
            Assert.Equal(first.Y, second.Y);
            Assert.Equal(332, second.X - first.X);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
