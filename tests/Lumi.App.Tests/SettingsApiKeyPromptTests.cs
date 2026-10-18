using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.App.Tests;

public sealed class SettingsApiKeyPromptTests
{
    private const string KeyName = "connection/dev-anthropic/api-key";

    private static (SettingsApiKeyPrompt Prompt, FakeSettingsWindowService Window) Create(SettingsHost host)
    {
        var window = new FakeSettingsWindowService();
        return (new SettingsApiKeyPrompt(host.Catalog, window), window);
    }

    [Fact]
    public async Task OpensSettingsAtWorkbenchConnection_FocusingKeyBox_WithReasonAsNotice()
    {
        using var host = new SettingsHost();
        var (prompt, window) = Create(host);

        await prompt.PromptAsync(ApiKeyPromptReason.Invalid);

        var nav = Assert.Single(window.Shown);
        Assert.Equal(SettingsSection.Connections, nav.Section);
        Assert.Equal("dev-anthropic", nav.ConnectionId);
        Assert.True(nav.FocusApiKey);
        Assert.Contains("無效", nav.Notice);
    }

    [Fact]
    public async Task Missing_KeySavedWhileOpen_ReturnsTrue()
    {
        using var host = new SettingsHost();
        var (prompt, window) = Create(host);
        window.WhileOpen = () =>
        {
            host.Secrets.Items[KeyName] = "sk-ant-new";
            return Task.CompletedTask;
        };

        Assert.True(await prompt.PromptAsync(ApiKeyPromptReason.Missing));
    }

    [Fact]
    public async Task Missing_ClosedWithoutKey_ReturnsFalse()
    {
        using var host = new SettingsHost();
        var (prompt, _) = Create(host);

        Assert.False(await prompt.PromptAsync(ApiKeyPromptReason.Missing));
    }

    [Fact]
    public async Task Invalid_ClosedWithoutChangingAnything_ReturnsFalse_SoNoRetryLoop()
    {
        using var host = new SettingsHost();
        host.Secrets.Items[KeyName] = "sk-ant-wrong";
        var (prompt, _) = Create(host);

        Assert.False(await prompt.PromptAsync(ApiKeyPromptReason.Invalid));
    }

    [Fact]
    public async Task Invalid_KeyReplacedWhileOpen_ReturnsTrue()
    {
        using var host = new SettingsHost();
        host.Secrets.Items[KeyName] = "sk-ant-wrong";
        var (prompt, window) = Create(host);
        window.WhileOpen = async () =>
        {
            var connection = host.Catalog.Current.Connections.Single();
            await host.Catalog.SaveConnectionAsync(connection, new SecretValue("sk-ant-right"));
        };

        Assert.True(await prompt.PromptAsync(ApiKeyPromptReason.Invalid));
    }

    [Fact]
    public async Task NoConnectionAtAll_ReturnsFalse()
    {
        using var host = new SettingsHost();
        await host.Catalog.DeleteConnectionAsync("dev-anthropic");
        var (prompt, window) = Create(host);

        Assert.False(await prompt.PromptAsync(ApiKeyPromptReason.Missing));
        Assert.Null(window.Shown.Single().ConnectionId);
    }
}
