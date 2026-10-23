using Lumi.Core.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Documents.Tests;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名

public class DocumentServiceCollectionTests
{
    [Fact]
    public void AddLumiDocuments_resolves_the_reader_and_one_format_reader_per_format()
    {
        using var provider = Build();

        Assert.IsType<DocumentReader>(provider.GetRequiredService<IDocumentReader>());

        var formats = provider.GetServices<IDocumentFormatReader>().Select(r => r.Format).ToList();
        Assert.Equal(4, formats.Count);
        Assert.Equal(
            new[] { DocumentFormat.Word, DocumentFormat.Excel, DocumentFormat.PowerPoint, DocumentFormat.Pdf }.OrderBy(f => f),
            formats.OrderBy(f => f));
    }

    [Fact]
    public void Default_options_match_the_documented_limits()
    {
        using var provider = Build();

        var options = provider.GetRequiredService<DocumentOptions>();

        Assert.Equal(50L * 1024 * 1024, options.MaxFileBytes);
        Assert.Equal(TimeSpan.FromSeconds(60), options.ReadTimeout);
    }

    [Fact]
    public void Reader_registered_by_the_extension_resolves_the_same_supported_extensions()
    {
        using var provider = Build();

        var reader = provider.GetRequiredService<IDocumentReader>();

        Assert.True(reader.Supports("x.docx"));
        Assert.True(reader.Supports("x.pdf"));
        Assert.False(reader.Supports("x.doc"));
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddLumiDocuments();
        return services.BuildServiceProvider();
    }
}
