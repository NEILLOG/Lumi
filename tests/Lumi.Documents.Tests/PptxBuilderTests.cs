#nullable disable   // OpenXml SDK 的導覽屬性標成可為 null；測試直接取值，失敗時以 NullReferenceException 呈現
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Lumi.Documents.Tests.Samples;

namespace Lumi.Documents.Tests;

public class PptxBuilderTests
{
    [Fact]
    public void Generated_file_reopens_with_the_openxml_sdk()
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);

        using var doc = PresentationDocument.Open(path, isEditable: false);

        Assert.NotNull(doc.PresentationPart);
    }

    [Fact]
    public void Deck_has_three_slides_with_their_titles()
    {
        using var stream = new MemoryStream(PptxBuilder.CreateBytes());
        using var doc = PresentationDocument.Open(stream, isEditable: false);
        var slides = SlideParts(doc);

        Assert.Equal(3, slides.Count);
        Assert.Equal(PptxBuilder.Slide1Title, TitleOf(slides[0]));
        Assert.Equal(PptxBuilder.Slide2Title, TitleOf(slides[1]));
        Assert.Equal(PptxBuilder.Slide3Title, TitleOf(slides[2]));
    }

    [Fact]
    public void Second_slide_has_a_two_by_two_table()
    {
        using var stream = new MemoryStream(PptxBuilder.CreateBytes());
        using var doc = PresentationDocument.Open(stream, isEditable: false);
        var table = SlideParts(doc)[1].Slide.Descendants<DocumentFormat.OpenXml.Drawing.Table>().Single();

        var rows = table.Elements<DocumentFormat.OpenXml.Drawing.TableRow>()
            .Select(r => r.Elements<DocumentFormat.OpenXml.Drawing.TableCell>().Select(c => c.InnerText).ToArray())
            .ToArray();
        Assert.Equal(PptxBuilder.Slide2TableRows, rows);
    }

    [Fact]
    public void Third_slide_has_speaker_notes()
    {
        using var stream = new MemoryStream(PptxBuilder.CreateBytes());
        using var doc = PresentationDocument.Open(stream, isEditable: false);
        var slides = SlideParts(doc);

        Assert.NotNull(slides[2].NotesSlidePart);
        Assert.Contains(PptxBuilder.Slide3Notes, slides[2].NotesSlidePart!.NotesSlide.InnerText);
        Assert.Null(slides[0].NotesSlidePart);
    }

    [Fact]
    public void Chinese_text_is_present_on_the_first_slide()
    {
        using var stream = new MemoryStream(PptxBuilder.CreateBytes());
        using var doc = PresentationDocument.Open(stream, isEditable: false);

        Assert.Contains(PptxBuilder.Slide1Body, SlideParts(doc)[0].Slide.InnerText);
    }

    private static List<SlidePart> SlideParts(PresentationDocument doc)
    {
        var ids = doc.PresentationPart!.Presentation.SlideIdList!.Elements<SlideId>();
        return ids.Select(id => (SlidePart)doc.PresentationPart.GetPartById(id.RelationshipId!)).ToList();
    }

    private static string TitleOf(SlidePart slide) =>
        slide.Slide.Descendants<Shape>()
            .First(s => s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Type?.Value == PlaceholderValues.Title)
            .InnerText;
}
