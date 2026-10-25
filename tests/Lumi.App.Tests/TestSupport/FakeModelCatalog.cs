using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Core.Web;

namespace Lumi.App.Tests.TestSupport;

/// <summary>只有讀取的假目錄；寫入的行為由 Infrastructure 的 JsonModelCatalog 測試涵蓋。</summary>
internal sealed class FakeModelCatalog : IModelCatalog
{
    public static readonly ConnectionSettings Connection = new("test-connection", "測試", ProviderFormat.Anthropic, "https://api.test");
    public static readonly ModelSettings Model = new("test-model-settings", Connection.Id, "test-model", "測試模型", 1024, 200_000, true);

    public FakeModelCatalog(bool withModel = true)
    {
        Current = withModel
            ? new AppSettings([Connection], [Model], Model.Id, Model.Id, ThemePreference.System)
            : AppSettings.Empty;
    }

    public AppSettings Current { get; set; }

    public string? LoadProblem { get; set; }

    public event EventHandler<AppSettings>? Changed;

    public void Raise() => Changed?.Invoke(this, Current);

    public ResolvedModel? Resolve(string? modelSettingsId)
    {
        var model = Current.Models.FirstOrDefault(m => m.Id == modelSettingsId);
        var connection = model is null ? null : Current.Connections.FirstOrDefault(c => c.Id == model.ConnectionId);
        return connection is null ? null : new ResolvedModel(model!, connection);
    }

    public List<(ConnectionSettings Connection, SecretValue? Key)> SavedConnections { get; } = [];

    public Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct = default)
    {
        SavedConnections.Add((connection, newApiKey));
        return Task.CompletedTask;
    }

    public Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct = default) => Task.FromResult(false);

    public Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SaveModelAsync(ModelSettings model, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default) => throw new NotSupportedException();

    public Task SetThemeIdAsync(string themeId, CancellationToken ct = default)
    {
        Current = Current with { ThemeId = Lumi.Core.Models.ThemePalettes.Normalize(themeId) };
        Raise();
        return Task.CompletedTask;
    }

    /// <summary>每次寫入的視窗位置（沒有 <see cref="Changed"/> 通知，和正式實作一樣）。</summary>
    public List<WindowSettings> WindowSaves { get; } = [];

    public Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct = default)
    {
        WindowSaves.Add(window);
        Current = Current with { WorkbenchWindow = window.Normalize() };
        return Task.CompletedTask;
    }

    /// <summary><see cref="HasProxyPasswordAsync"/> 的回傳值（M17）。</summary>
    public bool HasProxyPassword { get; set; }

    public Task SetNetworkAsync(NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default)
    {
        Current = Current with { Network = settings.Normalize() };
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task<bool> HasProxyPasswordAsync(CancellationToken ct = default) => Task.FromResult(HasProxyPassword);

    public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default)
    {
        var list = Current.McpServers.Where(m => m.Id != server.Id).Append(server).ToList();
        Current = Current with { McpServers = list };
        Raise();
        return Task.CompletedTask;
    }

    public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default)
    {
        Current = Current with { McpServers = Current.McpServers.Where(m => m.Id != serverId).ToList() };
        Raise();
        return Task.CompletedTask;
    }

    public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(McpServerSecrets.Empty);

    /// <summary>每次 <see cref="SetToolsAsync"/> 收到的值。</summary>
    public List<ToolSettings> ToolSaves { get; } = [];

    public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default)
    {
        var normalized = tools.Normalize();
        ToolSaves.Add(normalized);
        Current = Current with { Tools = normalized };
        Raise();
        return Task.CompletedTask;
    }

    /// <summary>每次 <see cref="SetMemoryEnabledAsync"/> 收到的值。</summary>
    public List<bool> MemoryEnabledSaves { get; } = [];

    public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        MemoryEnabledSaves.Add(enabled);
        Current = Current with { Memory = new Lumi.Core.Models.MemorySettings(enabled) };
        Raise();
        return Task.CompletedTask;
    }

    /// <summary>每次 <see cref="SetNotesFolderAsync"/> 收到的資料夾（原文）。</summary>
    public List<string> NotesFolderSaves { get; } = [];

    public Task SetNotesFolderAsync(string folder, CancellationToken ct = default)
    {
        NotesFolderSaves.Add(folder);
        Current = Current with { Notes = new Lumi.Core.Models.NotesSettings(folder.Trim()) };
        Raise();
        return Task.CompletedTask;
    }

    public Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct = default)
    {
        Current = Current with { Sprite = sprite.Normalize() };
        Raise();
        return Task.CompletedTask;
    }

    /// <summary>每次 <see cref="SetSpeechAsync"/> 的參數（端點、語言、逾時，以及 Token；沒有輸入新 Token 時是 null）。</summary>
    public List<(SpeechSettings Settings, SecretValue? Token)> SpeechSaves { get; } = [];

    public Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default)
    {
        SpeechSaves.Add((settings, newToken));
        Current = Current with { Speech = settings.Normalize() };
        return Task.CompletedTask;
    }

    public Task<bool> HasSpeechTokenAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task ClearSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default)
    {
        Current = Current with { Persona = persona };
        Raise();
        return Task.CompletedTask;
    }
}
