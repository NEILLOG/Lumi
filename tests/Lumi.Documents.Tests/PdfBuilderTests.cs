using Lumi.Documents.Tests.Samples;
using UglyToad.PdfPig;

namespace Lumi.Documents.Tests;

public class PdfBuilderTests
{
    [Fact]
    public void Generated_file_opens_with_pdfpig_and_has_three_pages()
    {
        using var document = PdfDocument.Open(PdfBuilder.CreateBytes());

        Assert.Equal(3, document.NumberOfPages);
    }

    [Fact]
    public void Chinese_page_text_is_extracted()
    {
        using var document = PdfDocument.Open(PdfBuilder.CreateBytes());

        Assert.Contains("中文", document.GetPage(1).Text);
    }

    [Fact]
    public void English_page_text_is_extracted()
    {
        using var document = PdfDocument.Open(PdfBuilder.CreateBytes());

        Assert.Contains("English page", document.GetPage(2).Text);
    }

    [Fact]
    public void Third_page_is_blank()
    {
        using var document = PdfDocument.Open(PdfBuilder.CreateBytes());

        Assert.True(string.IsNullOrWhiteSpace(document.GetPage(3).Text));
    }

    [Fact]
    public void Create_writes_the_file_into_the_given_folder()
    {
        using var folder = new TempFolder();

        var path = PdfBuilder.Create(folder.Path, "named.pdf");

        Assert.True(File.Exists(path));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path)[..4]));
    }
}
