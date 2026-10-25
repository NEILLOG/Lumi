using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>網路設定（M17 17D）在 settings.json 的讀寫、正規化、Proxy 密碼只進 ISecretStore。</summary>
public sealed class JsonModelCatalogNetworkTests : IDisposable
{
    private const string PlainPassword = "proxy-password-PLAIN-MARKER-7777";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-network-tests-" + Guid.NewGuid().ToString("N"));
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

    private static NetworkSettings Pac() => new()
    {
        Mode = ProxyMode.Pac,
        PacUrl = "http://wpad.corp/proxy.pac",
        Auth = ProxyAuthMode.Integrated,
        IntegratedAuthSites = ["*.corp.example.com"],
        TimeoutSeconds = 45,
    };

    private static NetworkSettings Manual() => new()
    {
        Mode = ProxyMode.Manual,
        ManualProxy = "proxy.example.com:8080",
        BypassList = ["*.example.com", "10.*"],
        Auth = ProxyAuthMode.Integrated,
    };

    private static NetworkSettings Account(string user = @"CORP\amy") => new()
    {
        Mode = ProxyMode.System,
        Auth = ProxyAuthMode.Account,
        ProxyUser = user,
    };

    /// <summary>
    /// <see cref="NetworkSettings"/> 的清單屬性是 <c>IReadOnlyList</c>，record 的 Equals 比的是參考，
    /// 內容一樣也會不相等，所以比較時用序列化後的文字（內容相同就相同）。
    /// </summary>
    private static void AssertSameNetwork(NetworkSettings expected, NetworkSettings actual) =>
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));

    private SecretValue? PasswordValue =>
        _secrets.Items.TryGetValue(NetworkSecrets.ProxyPassword.Name, out var v) ? new SecretValue(v) : null;

    [Theory]
    [InlineData(ProxyMode.System)]
    [InlineData(ProxyMode.Pac)]
    [InlineData(ProxyMode.Manual)]
    [InlineData(ProxyMode.None)]
    public async Task Each_proxy_mode_survives_reload(ProxyMode mode)
    {
        var settings = mode switch
        {
            ProxyMode.Pac => Pac(),
            ProxyMode.Manual => Manual(),
            _ => new NetworkSettings { Mode = mode, Auth = ProxyAuthMode.Integrated, TimeoutSeconds = 60 },
        };

        await NewCatalog().SetNetworkAsync(settings, null);

        var reloaded = NewCatalog().Current.Network;
        AssertSameNetwork(settings.Normalize(), reloaded);
    }

    [Fact]
    public async Task Account_settings_survive_reload_and_the_password_is_not_in_the_file()
    {
        await NewCatalog().SetNetworkAsync(Account(), new SecretValue(PlainPassword));

        var reloaded = NewCatalog();
        Assert.Equal(ProxyAuthMode.Account, reloaded.Current.Network.Auth);
        Assert.Equal(@"CORP\amy", reloaded.Current.Network.ProxyUser);
        Assert.True(await reloaded.HasProxyPasswordAsync());

        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain(PlainPassword, text);
        Assert.Contains("CORP", text);
        Assert.Equal(PlainPassword, _secrets.Items[NetworkSecrets.ProxyPassword.Name]);
    }

    [Fact]
    public void The_example_from_the_spec_is_read_correctly()
    {
        WriteSettingsFile("""
            {"version":1,"connections":[],"models":[],"theme":"system",
             "network":{"mode":"pac","pacUrl":"http://wpad.corp/proxy.pac","manualProxy":"","bypassList":[],
                        "auth":"integrated","proxyUser":"","integratedAuthSites":["*.corp.example.com"],"timeoutSeconds":30}}
            """);

        var network = NewCatalog().Current.Network;

        Assert.Equal(ProxyMode.Pac, network.Mode);
        Assert.Equal("http://wpad.corp/proxy.pac", network.PacUrl);
        Assert.Equal(ProxyAuthMode.Integrated, network.Auth);
        Assert.Equal(["*.corp.example.com"], network.IntegratedAuthSites);
        Assert.Equal(30, network.TimeoutSeconds);
    }

    [Fact]
    public void Old_settings_file_without_network_uses_the_default()
    {
        WriteSettingsFile("""{"version":1,"connections":[],"models":[],"theme":"system"}""");

        var catalog = NewCatalog();

        Assert.Null(catalog.LoadProblem);
        AssertSameNetwork(NetworkSettings.Default, catalog.Current.Network);
        Assert.Equal(ProxyMode.System, catalog.Current.Network.Mode);
    }

    [Fact]
    public void Network_that_is_not_an_object_uses_the_default_and_keeps_the_rest_of_the_file()
    {
        WriteSettingsFile("""{"version":1,"connections":[],"models":[],"theme":"dark","network":"壞掉了"}""");

        var catalog = NewCatalog();

        Assert.Null(catalog.LoadProblem);
        AssertSameNetwork(NetworkSettings.Default, catalog.Current.Network);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
    }

    [Fact]
    public void A_field_with_the_wrong_type_uses_its_default_and_the_other_fields_still_load()
    {
        WriteSettingsFile("""
            {"version":1,"connections":[],"models":[],"theme":"system",
             "network":{"mode":"pac","pacUrl":123,"timeoutSeconds":"很快","integratedAuthSites":["a.test"]}}
            """);

        var network = NewCatalog().Current.Network;

        Assert.Equal(ProxyMode.Pac, network.Mode);
        Assert.Equal("", network.PacUrl);
        Assert.Equal(NetworkSettings.DefaultTimeoutSeconds, network.TimeoutSeconds);
        Assert.Equal(["a.test"], network.IntegratedAuthSites);
    }

    [Fact]
    public void Unknown_enum_text_uses_the_default_for_that_field()
    {
        WriteSettingsFile("""
            {"version":1,"connections":[],"models":[],"theme":"system","network":{"mode":"bogus","auth":"kerberos-ish"}}
            """);

        var network = NewCatalog().Current.Network;

        Assert.Equal(ProxyMode.System, network.Mode);
        Assert.Equal(ProxyAuthMode.Integrated, network.Auth);
    }

    [Fact]
    public void Lists_are_cleaned_when_loaded()
    {
        WriteSettingsFile("""
            {"version":1,"connections":[],"models":[],"theme":"system",
             "network":{"integratedAuthSites":["  *.Corp.Example.com ", "", "*.corp.example.com", "intranet.test"],
                        "bypassList":["  ", "B.test", "b.test"]}}
            """);

        var network = NewCatalog().Current.Network;

        Assert.Equal(["*.corp.example.com", "intranet.test"], network.IntegratedAuthSites);
        Assert.Equal(["b.test"], network.BypassList);
    }

    [Fact]
    public async Task Lists_are_cleaned_when_saved_and_survive_reload()
    {
        var settings = new NetworkSettings
        {
            Mode = ProxyMode.Manual,
            ManualProxy = "  proxy.example.com:8080  ",
            BypassList = ["Z.test", "", "*.example.com", "z.TEST"],
            Auth = ProxyAuthMode.Integrated,
            IntegratedAuthSites = ["intranet.test", "INTRANET.test"],
        };

        await NewCatalog().SetNetworkAsync(settings, null);

        var reloaded = NewCatalog().Current.Network;
        Assert.Equal("proxy.example.com:8080", reloaded.ManualProxy);
        Assert.Equal(["*.example.com", "z.test"], reloaded.BypassList);
        Assert.Equal(["intranet.test"], reloaded.IntegratedAuthSites);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(30, 30)]
    [InlineData(999, 120)]
    public async Task Timeout_is_clamped_and_survives_reload(int requested, int expected)
    {
        await NewCatalog().SetNetworkAsync(new NetworkSettings { TimeoutSeconds = requested }, null);

        Assert.Equal(expected, NewCatalog().Current.Network.TimeoutSeconds);
    }

    [Fact]
    public void Timeout_from_a_file_is_clamped_on_load()
    {
        WriteSettingsFile("""{"version":1,"connections":[],"models":[],"theme":"system","network":{"timeoutSeconds":5000}}""");

        Assert.Equal(NetworkSettings.MaxTimeoutSeconds, NewCatalog().Current.Network.TimeoutSeconds);
    }

    [Fact(Skip = "待 17A 合併後才能驗證：NetworkRules.Validate 目前是 17D 的暫時佔位（只回 null）")]
    public async Task Validation_error_throws_ArgumentException_and_changes_nothing()
    {
        var catalog = NewCatalog();
        await catalog.SetNetworkAsync(Pac(), null);
        var before = File.ReadAllText(SettingsPath);

        var invalid = Pac() with { PacUrl = "not a url" };
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SetNetworkAsync(invalid, null));

        AssertSameNetwork(Pac().Normalize(), catalog.Current.Network);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task Switching_away_from_account_deletes_the_stored_password()
    {
        var catalog = NewCatalog();
        await catalog.SetNetworkAsync(Account(), new SecretValue(PlainPassword));
        Assert.True(await catalog.HasProxyPasswordAsync());

        await catalog.SetNetworkAsync(new NetworkSettings { Auth = ProxyAuthMode.Integrated }, null);

        Assert.False(await catalog.HasProxyPasswordAsync());
        Assert.Null(PasswordValue);
    }

    [Fact]
    public async Task Account_without_a_new_password_keeps_the_stored_password()
    {
        var catalog = NewCatalog();
        await catalog.SetNetworkAsync(Account(), new SecretValue(PlainPassword));

        await catalog.SetNetworkAsync(Account(@"CORP\bob"), null);

        Assert.Equal(PlainPassword, PasswordValue!.Reveal());
        Assert.Equal(@"CORP\bob", catalog.Current.Network.ProxyUser);
    }

    [Fact]
    public async Task Account_with_a_new_password_replaces_the_stored_password()
    {
        var catalog = NewCatalog();
        await catalog.SetNetworkAsync(Account(), new SecretValue(PlainPassword));

        await catalog.SetNetworkAsync(Account(), new SecretValue("new-password-PLAIN-8888"));

        Assert.Equal("new-password-PLAIN-8888", PasswordValue!.Reveal());
    }

    [Fact]
    public async Task Integrated_auth_never_stores_a_password()
    {
        var catalog = NewCatalog();

        await catalog.SetNetworkAsync(Pac(), new SecretValue(PlainPassword));

        Assert.False(await catalog.HasProxyPasswordAsync());
        Assert.DoesNotContain(PlainPassword, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task Changed_is_raised_with_the_saved_network_settings()
    {
        var catalog = NewCatalog();
        AppSettings? seen = null;
        catalog.Changed += (_, s) => seen = s;

        await catalog.SetNetworkAsync(Manual(), null);

        Assert.NotNull(seen);
        AssertSameNetwork(Manual().Normalize(), seen!.Network);
    }
}
