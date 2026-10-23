#nullable disable   // OpenXml SDK 的導覽屬性標成可為 null；測試直接取值，失敗時以 NullReferenceException 呈現
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lumi.Documents.Tests.Samples;

namespace Lumi.Documents.Tests;

public class DocxBuilderTests
{
    [Fact]
    public void Generated_file_reopens_with_the_openxml_sdk()
    {
        using var folder = new TempFolder();
        var path = DocxBuilder.Create(folder.Path);

        using var doc = WordprocessingDocument.Open(path, isEditable: false);

        Assert.NotNull(doc.MainDocumentPart);
    }

    [Fact]
    public void Headings_paragraphs_and_bullets_are_present_in_order()
    {
        using var stream = new MemoryStream(DocxBuilder.CreateBytes());
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var paragraphs = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().ToList();

        Assert.Equal(DocxBuilder.Title, paragraphs[0].InnerText);
        Assert.Equal("Heading1", paragraphs[0].ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Equal(DocxBuilder.Paragraph, paragraphs[1].InnerText);
        Assert.Null(paragraphs[1].ParagraphProperties?.ParagraphStyleId);
        Assert.Equal("Heading2", paragraphs[2].ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Equal(DocxBuilder.Bullet1, paragraphs[3].InnerText);
        Assert.Equal(1, paragraphs[3].ParagraphProperties?.NumberingProperties?.NumberingId?.Val?.Value);
        Assert.Equal(DocxBuilder.Bullet2, paragraphs[4].InnerText);
        Assert.Equal(DocxBuilder.Closing, paragraphs[^1].InnerText);
    }

    [Fact]
    public void Table_is_two_rows_by_three_columns()
    {
        using var stream = new MemoryStream(DocxBuilder.CreateBytes());
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var table = doc.MainDocumentPart!.Document.Body!.Elements<Table>().Single();

        var rows = table.Elements<TableRow>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(3, row.Elements<TableCell>().Count()));
        Assert.Equal(DocxBuilder.TableRows, rows.Select(r => r.Elements<TableCell>().Select(c => c.InnerText).ToArray()).ToArray());
    }

    [Fact]
    public void Chinese_and_emoji_text_survive_the_round_trip()
    {
        using var stream = new MemoryStream(DocxBuilder.CreateBytes());
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var text = doc.MainDocumentPart!.Document.Body!.InnerText;

        Assert.Contains("中文", text);
        Assert.Contains("😀", text);
    }

    [Fact]
    public void Create_writes_the_file_into_the_given_folder()
    {
        using var folder = new TempFolder();

        var path = DocxBuilder.Create(folder.Path, "named.docx");

        Assert.Equal(Path.Combine(folder.Path, "named.docx"), path);
        Assert.True(File.Exists(path));
    }
}
