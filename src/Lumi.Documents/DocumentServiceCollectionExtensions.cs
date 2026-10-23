using Lumi.Core.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Documents;

public static class DocumentServiceCollectionExtensions
{
    /// <summary>註冊文件讀取（<see cref="IDocumentReader"/>）、四種格式的讀取器與 <see cref="DocumentOptions"/>（沒有註冊過的才用預設值）。</summary>
    public static IServiceCollection AddLumiDocuments(this IServiceCollection services)
    {
        services.TryAddSingleton(new DocumentOptions());
        services.AddSingleton<IDocumentFormatReader, WordDocumentReader>();
        services.AddSingleton<IDocumentFormatReader, ExcelDocumentReader>();
        services.AddSingleton<IDocumentFormatReader, PowerPointDocumentReader>();
        services.AddSingleton<IDocumentFormatReader, PdfDocumentReader>();
        services.AddSingleton<IDocumentReader, DocumentReader>();
        return services;
    }
}
