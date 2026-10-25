using System.Text.Json;
using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>瀏覽器設定（M18 18J）在 settings.json 的讀寫、正規化、舊檔與壞檔的預設值。</summary>
public sealed class JsonModelCatalogBrowserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-browser-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), _secrets, NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private void WriteSettingsFile(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
    }

    [Theory]
    [InlineData(BrowserChoice.Auto, false)]
    [InlineData(BrowserChoice.Edge, true)]
    [InlineData(BrowserChoice.Chrome, false)]
    [InlineData(BrowserChoice.Chrome, true)]
    public async Task Browser_settings_survive_reload(BrowserChoice choice, bool showWindow)
    {
        var settings = new BrowserSettings { Choice = choice, ShowWindow = showWindow };

        await NewCatalog().SetBrowserAsync(settings);

        Assert.Equal(settings, NewCatalog().Current.Browser);
    }

    [Fact]
    public async Task The_file_stores_only_choice_and_show_window_in_camel_case()
    {
        await NewCatalog().SetBrowserAsync(new BrowserSettings { Choice = BrowserChoice.Edge, ShowWindow = true });

        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        var browser = doc.RootElement.GetProperty("browser");
        var names = browser.EnumerateObject().Select(p => p.Name).ToArray();

        Assert.Equal(["choice", "showWindow"], names);
        Assert.Equal("edge", browser.GetProperty("choice").GetString());
        Assert.True(browser.GetProperty("showWindow").GetBoolean());
    }

    [Fact]
    public void Old_settings_file_without_browser_uses_the_default()
    {
        WriteSettingsFile("""{"version":1,"connections":[],"models":[],"theme":"system"}""");

        var catalog = NewCatalog();

        Assert.Null(catalog.LoadProblem);
        Assert.Equal(BrowserSettings.Default, catalog.Current.Browser);
        Assert.Equal(BrowserChoice.Auto, catalog.Current.Browser.Choice);
        Assert.False(catalog.Current.Browser.ShowWindow);
    }

    [Fact]
    public void Browser_that_is_not_an_object_uses_the_default_and_keeps_the_rest_of_the_file()
    {
        WriteSettingsFile("""{"version":1,"connections":[],"models":[],"theme":"dark","browser":"壞掉了"}""");

        var catalog = NewCatalog();

        Assert.Null(catalog.LoadProblem);
        Assert.Equal(BrowserSettings.Default, catalog.Current.Browser);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
    }

    [Fact]
    public void A_field_with_the_wrong_type_uses_its_default_and_the_other_field_still_loads()
    {
        WriteSettingsFile("""{"version":1,"connections":[],"models":[],"theme":"system","browser":{"choice":"chrome","showWindow":"是"}}""");

        var browser = NewCatalog().Current.Browser;

        Assert.Equal(BrowserChoice.Chrome, browser.Choice);
        Assert.False(browser.ShowWindow);
    }

    [Fact]
    public void Unknown_choice_text_uses_auto()
    {
        WriteSettingsFile("""{"version":1,"connections":[],"models":[],"theme":"system","browser":{"choice":"safari","showWindow":true}}""");

        var browser = NewCatalog().Current.Browser;

        Assert.Equal(BrowserChoice.Auto, browser.Choice);
        Assert.True(browser.ShowWindow);
    }

    [Fact]
    public async Task Changed_is_raised_with_the_saved_browser_settings()
    {
        var catalog = NewCatalog();
        AppSettings? seen = null;
        catalog.Changed += (_, s) => seen = s;

        await catalog.SetBrowserAsync(new BrowserSettings { Choice = BrowserChoice.Chrome, ShowWindow = true });

        Assert.NotNull(seen);
        Assert.Equal(new BrowserSettings { Choice = BrowserChoice.Chrome, ShowWindow = true }, seen!.Browser);
    }
}
