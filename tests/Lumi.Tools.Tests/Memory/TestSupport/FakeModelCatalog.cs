using Lumi.Core.Browser;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Core.Web;

namespace Lumi.Tools.Tests.Memory;

/// <summary>只有 <see cref="Current"/> 有意義的假目錄：記憶工具只看 <c>Current.Memory.Enabled</c>。其他成員不會被呼叫。</summary>
internal sealed class FakeModelCatalog(bool memoryEnabled = true) : IModelCatalog
{
    public AppSettings Current { get; set; } = AppSettings.Empty with { Memory = new MemorySettings(memoryEnabled) };

    public string? LoadProblem => null;

    public event EventHandler<AppSettings>? Changed
    {
        add { }
        remove { }
    }

    /// <summary><see cref="HasProxyPasswordAsync"/> 的回傳值（M17）。</summary>
    public bool HasProxyPassword { get; set; }

    public Task SetBrowserAsync(BrowserSettings settings, CancellationToken ct = default)
    {
        Current = Current with { Browser = settings.Normalize() };
        return Task.CompletedTask;
    }

    public Task SetNetworkAsync(NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default)
    {
        Current = Current with { Network = settings.Normalize() };
        return Task.CompletedTask;
    }

    public Task<bool> HasProxyPasswordAsync(CancellationToken ct = default) => Task.FromResult(HasProxyPassword);

    public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default)
    {
        var list = Current.McpServers.Where(m => m.Id != server.Id).Append(server).ToList();
        Current = Current with { McpServers = list };
        return Task.CompletedTask;
    }

    public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default)
    {
        Current = Current with { McpServers = Current.McpServers.Where(m => m.Id != serverId).ToList() };
        return Task.CompletedTask;
    }

    public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(McpServerSecrets.Empty);

    public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default)
    {
        Current = Current with { Tools = tools.Normalize() };
        return Task.CompletedTask;
    }

    public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        Current = Current with { Memory = new MemorySettings(enabled) };
        return Task.CompletedTask;
    }

    public ResolvedModel? Resolve(string? modelSettingsId) => null;

    public Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveModelAsync(ModelSettings model, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetThemeIdAsync(string themeId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task ClearSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetNotesFolderAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
}
