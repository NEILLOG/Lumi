using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Lumi.Core;
using Lumi.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lumi.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddLumiCore();
        builder.Services.AddLumiPlatform();
        builder.Services.AddLumiApp();

        using var host = builder.Build();
        host.Start();
        try
        {
            BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync().GetAwaiter().GetResult();
        }
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Lumi#IBM Plex Sans" })
            .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(
                new Uri("fonts:Lumi", UriKind.Absolute),
                new Uri("avares://Lumi.App/Assets/Fonts", UriKind.Absolute))))
            .LogToTrace();
}
