using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Logging;

public static class LumiFileLoggingExtensions
{
    /// <summary>把記錄另外寫進 <see cref="LumiPaths.LogDirectory"/>（M9 9C）。</summary>
    public static ILoggingBuilder AddLumiFileLog(this ILoggingBuilder builder, string? directory = null)
    {
        builder.Services.AddSingleton<ILoggerProvider>(new LumiFileLoggerProvider(directory ?? LumiPaths.LogDirectory));
        return builder;
    }
}
