using Lumi.Core.Browser;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Core.Web;

namespace Lumi.Mcp.Tests.TestSupport;

/// <summary>只有 MCP 相關的假設定目錄；其他方法不會被 Lumi.Mcp 呼叫，丟 <see cref="NotSupportedException"/>。</summary>
internal sealed class FakeMcpModelCatalog(params McpServerSettings[] servers) : IModelCatalog
{
    public AppSettings Current { get; private set; } = AppSettings.Empty with { McpServers = servers };

    public Dictionary<string, McpServerSecrets> Secrets { get; } = new(StringComparer.Ordinal);

    public string? LoadProblem => null;

    public event EventHandler<AppSettings>? Changed;

    /// <summary>換成新的伺服器清單並通知（模擬使用者儲存設定）。</summary>
    public void SetServers(params McpServerSettings[] servers)
    {
        Current = Current with { McpServers = servers };
        Changed?.Invoke(this, Current);
    }

    public void SetTools(ToolSettings tools)
    {
        Current = Current with { Tools = tools };
        Changed?.Invoke(this, Current);
    }

    /// <summary><see cref="HasProxyPasswordAsync"/> 的回傳值（M17）。</summary>
    public bool HasProxyPassword { get; set; }

    public Task SetBrowserAsync(BrowserSettings settings, CancellationToken ct = default)
    {
        Current = Current with { Browser = settings.Normalize() };
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task SetNetworkAsync(NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default)
    {
        Current = Current with { Network = settings.Normalize() };
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task<bool> HasProxyPasswordAsync(CancellationToken ct = default) => Task.FromResult(HasProxyPassword);

    public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default)
    {
        SetServers(Current.McpServers.Where(s => s.Id != server.Id).Append(server).ToArray());
        return Task.CompletedTask;
    }

    public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default)
    {
        SetServers(Current.McpServers.Where(s => s.Id != serverId).ToArray());
        return Task.CompletedTask;
    }

    public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(Secrets.GetValueOrDefault(serverId) ?? McpServerSecrets.Empty);

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
    public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default) => throw new NotSupportedException();
    public ResolvedModel? Resolve(string? modelSettingsId) => null;
}
