using Lumi.Core.Models;
using Lumi.Core.Secrets;

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

    public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default)
    {
        Current = Current with { Persona = persona };
        Raise();
        return Task.CompletedTask;
    }
}
