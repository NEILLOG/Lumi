using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Lumi.App.Services;
using Lumi.App.Views;
using Lumi.Core;
using Lumi.Documents;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Logging;
using Lumi.Mcp;
using Lumi.Platform;
using Lumi.Providers;
using Lumi.Speech;
using Lumi.Tools;
using Lumi.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lumi.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        IHost host;
        try
        {
            host = BuildHost(args);
            host.Start();
        }
        catch (Exception ex)
        {
            // DI 組裝或啟動失敗：沒有 DI 可用，直接顯示說明視窗（M9 9C）
            return RunStartupFailure(ex, args);
        }

        using (host)
        {
            try
            {
                return BuildAvaloniaApp(() => new App(host.Services)).StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                host.StopAsync().GetAwaiter().GetResult();
            }
        }
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.AddLumiFileLog();   // 記錄檔寫進使用者資料夾的 logs/（M9 9C）
        builder.Services.AddLumiCore();
        builder.Services.AddLumiPlatform(configureOverlay: options =>
        {
            // Lumi:Developer:OverlayLayered=false：Windows 上萬一加了 WS_EX_LAYERED 後透明渲染消失時的備案
            if (bool.TryParse(builder.Configuration["Lumi:Developer:OverlayLayered"], out var layered))
                options.Layered = layered;
        });
        builder.Services.AddLumiWeb();   // 網路基礎（M17）：Proxy 與網路工具；要在 AddHttpClient 的註冊之前
        builder.Services.AddLumiProviders();
        builder.Services.AddLumiSpeech();
        builder.Services.AddLumiDocuments();   // 文件讀取（M10 10C）：read_document 用
        builder.Services.AddLumiInfrastructure();
        builder.Services.AddLumiTools();
        builder.Services.AddLumiMcp();   // MCP 伺服器（M16）
        builder.Services.AddLumiApp();
        // 開發用開關：Lumi:Developer:ShowSpriteHud（小精靈殼的診斷 HUD 與開發選單）
        builder.Services.AddSingleton(new DeveloperOptions
        {
            ShowSpriteHud = bool.TryParse(builder.Configuration["Lumi:Developer:ShowSpriteHud"], out var showHud) && showHud,
        });
        return builder.Build();
    }

    private static int RunStartupFailure(Exception exception, string[] args)
    {
        var environment = StartupEnvironment.Current();
        var report = StartupFailureReport.Create(exception, environment);
        // 記錄檔直接建立（沒有 DI）；寫入失敗時記錄器自己吞掉，不影響說明視窗
        new LumiFileLoggerProvider(LumiPaths.LogDirectory).CreateLogger("Lumi.Startup")
            .LogCritical(exception, "啟動失敗（DI 組裝或主機啟動階段）");
        return BuildAvaloniaApp(() => new App(report)).StartWithClassicDesktopLifetime(args);
    }

    private static AppBuilder BuildAvaloniaApp(Func<App> create) =>
        AppBuilder.Configure(create)
            .UsePlatformDetect()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Lumi#IBM Plex Sans" })
            .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(
                new Uri("fonts:Lumi", UriKind.Absolute),
                new Uri("avares://Lumi.App/Assets/Fonts", UriKind.Absolute))))
            .LogToTrace();
}
