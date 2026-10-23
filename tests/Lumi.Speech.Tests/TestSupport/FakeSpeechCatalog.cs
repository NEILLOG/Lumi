using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.Speech.Tests.TestSupport;

/// <summary>只提供讀取語音設定的假目錄；寫入的行為由 Infrastructure 的 JsonModelCatalog 測試涵蓋。</summary>
internal sealed class FakeSpeechCatalog(SpeechSettings speech) : IModelCatalog
{
    public AppSettings Current { get; set; } = AppSettings.Empty with { Speech = speech };

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
    public Task SetNotesFolderAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    public ResolvedModel? Resolve(string? modelSettingsId) => null;
}
