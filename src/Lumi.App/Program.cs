using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Lumi.Core;
using Lumi.Infrastructure;
using Lumi.Platform;
using Lumi.Providers;
using Lumi.Tools;
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
        builder.Services.AddLumiPlatform(configureOverlay: options =>
        {
            // Lumi:Developer:OverlayLayered=false：Windows 上萬一加了 WS_EX_LAYERED 後透明渲染消失時的備案
            if (bool.TryParse(builder.Configuration["Lumi:Developer:OverlayLayered"], out var layered))
                options.Layered = layered;
        });
        builder.Services.AddLumiProviders();
        builder.Services.AddLumiInfrastructure();
        builder.Services.AddLumiTools(options =>
        {
            // appsettings／環境變數：Lumi:Tools:EnableDemoTools（M4 有真正的檔案工具後移除示範工具）
            if (bool.TryParse(builder.Configuration["Lumi:Tools:EnableDemoTools"], out var enableDemoTools))
                options.EnableDemoTools = enableDemoTools;
            options.NotesDirectory = builder.Configuration["Lumi:Tools:NotesDirectory"];
        });
        builder.Services.AddLumiApp();
        // 開發用開關：Lumi:Developer:ShowSpriteHud（小精靈殼的診斷 HUD 與開發選單）
        builder.Services.AddSingleton(new DeveloperOptions
        {
            ShowSpriteHud = bool.TryParse(builder.Configuration["Lumi:Developer:ShowSpriteHud"], out var showHud) && showHud,
        });

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
