using Microsoft.Extensions.Hosting;

namespace Lumi.App.Tests;

/// <summary>M22：單檔發佈旁邊可能沒有 appsettings.json，預設值要內建在程式裡。</summary>
public sealed class DefaultSettingsTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "lumi-settings-" + Guid.NewGuid().ToString("N"));

    public DefaultSettingsTests() => Directory.CreateDirectory(_contentRoot);

    public void Dispose() => Directory.Delete(_contentRoot, recursive: true);

    private HostApplicationBuilder Build()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = _contentRoot });
        Program.ApplyDefaultSettings(builder.Configuration);
        return builder;
    }

    [Fact]
    public void WithoutAnAppsettingsFile_TheBuiltInDefaultsApply()
    {
        var configuration = Build().Configuration;

        Assert.Equal("Information", configuration["Logging:LogLevel:Default"]);
        Assert.Equal("Warning", configuration["Logging:LogLevel:Microsoft"]);
        Assert.Equal("false", configuration["Lumi:Developer:ShowSpriteHud"]);
        Assert.Equal("true", configuration["Lumi:Developer:OverlayLayered"]);
    }

    [Fact]
    public void AnAppsettingsFileNextToTheExecutable_StillOverridesTheDefaults()
    {
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"),
            """{ "Logging": { "LogLevel": { "Default": "Debug" } }, "Lumi": { "Developer": { "ShowSpriteHud": true } } }""");

        var configuration = Build().Configuration;

        Assert.Equal("Debug", configuration["Logging:LogLevel:Default"]);
        Assert.Equal("true", configuration["Lumi:Developer:ShowSpriteHud"], ignoreCase: true);
        Assert.Equal("Warning", configuration["Logging:LogLevel:Microsoft"]);   // 沒覆寫的維持預設
    }
}
