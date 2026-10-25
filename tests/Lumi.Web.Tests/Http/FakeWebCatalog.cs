using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Core.Web;

namespace Lumi.Web.Tests.Http;

/// <summary>只提供讀取網路設定的假目錄。</summary>
internal sealed class FakeWebCatalog(NetworkSettings network) : IModelCatalog
{
    public AppSettings Current { get; set; } = AppSettings.Empty with { Network = network };

    public string? LoadProblem => null;

    public event EventHandler<AppSettings>? Changed { add { } remove { } }

    public Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> HasSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task ClearSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
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
    /// <summary><see cref="HasProxyPasswordAsync"/> 的回傳值（M17）。</summary>
    public bool HasProxyPassword { get; set; }

    public Task SetNetworkAsync(NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default)
    {
        Current = Current with { Network = settings.Normalize() };
        return Task.CompletedTask;
    }

    public Task<bool> HasProxyPasswordAsync(CancellationToken ct = default) => Task.FromResult(HasProxyPassword);

    public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetNotesFolderAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    public ResolvedModel? Resolve(string? modelSettingsId) => null;
}
