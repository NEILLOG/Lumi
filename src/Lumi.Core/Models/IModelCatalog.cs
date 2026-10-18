using Lumi.Core.Secrets;

namespace Lumi.Core.Models;

/// <summary>
/// 連線與模型設定（兩層、各自多筆）、預設模型、主題。<see cref="Current"/> 是不可變快照，
/// 每次儲存換一份新的，讀的人不需要上鎖。金鑰只經過這裡寫進 <see cref="ISecretStore"/>，不會存進設定檔。
/// </summary>
public interface IModelCatalog
{
    AppSettings Current { get; }

    /// <summary>載入設定時遇到的問題（檔案損毀、版本太新），給工作台提示一次；沒有問題時是 null。</summary>
    string? LoadProblem { get; }

    event EventHandler<AppSettings>? Changed;

    /// <param name="newApiKey">null 表示金鑰不變。</param>
    Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct = default);

    Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default);

    /// <summary>連同底下的模型設定與金鑰一起刪除。</summary>
    Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default);

    Task SaveModelAsync(ModelSettings model, CancellationToken ct = default);

    Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default);

    Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default);

    Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default);

    Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default);

    /// <summary>找不到（被刪了，或連線不見了）時回 null。</summary>
    ResolvedModel? Resolve(string? modelSettingsId);
}
