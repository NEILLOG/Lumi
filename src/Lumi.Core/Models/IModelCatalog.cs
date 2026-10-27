using Lumi.Core.Browser;
using Lumi.Core.Mcp;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Core.Web;

namespace Lumi.Core.Models;

/// <summary>
/// 連線與模型設定（兩層、各自多筆）、預設模型、主題、人設。<see cref="Current"/> 是不可變快照，
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

    /// <summary>連線有沒有金鑰。只查存不存在、不讀內容（Mac 不會跳鑰匙圈授權視窗）。</summary>
    Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct = default);

    Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default);

    /// <summary>連同底下的模型設定與金鑰一起刪除。</summary>
    Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default);

    Task SaveModelAsync(ModelSettings model, CancellationToken ct = default);

    Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default);

    Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default);

    Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default);

    Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default);

    /// <summary>主題色票；不認得的 ID 會存成預設（<see cref="ThemePalettes.Normalize"/>）。</summary>
    Task SetThemeIdAsync(string themeId, CancellationToken ct = default);

    /// <summary>自訂人設超過 <see cref="PersonaSettings.CustomPersonaMaxLength"/> 字時丟 <see cref="ArgumentException"/>。</summary>
    Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default);

    /// <summary>小精靈的角色、尺寸、位置等（M7）。</summary>
    Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct = default);

    /// <summary>
    /// 工作台視窗的位置與大小（M9 9D）。存在 <c>settings.json</c>，和小精靈設定同一個檔案；不通知 <see cref="Changed"/>
    /// （拖曳時會頻繁寫入，不要讓設定頁等訂閱者跟著重載）。<see cref="AppSettings.WorkbenchWindow"/> 是讀取的值。
    /// </summary>
    Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct = default);

    /// <summary>
    /// 語音服務設定（M6）。Token 只寫進 <see cref="ISecretStore"/>：端點的 Host 與 <see cref="SpeechSettings.TokenHost"/> 不同時視為沒有 Token，
    /// 沒帶新 Token 就刪除舊的，避免舊 Token 被送到新主機。<paramref name="newToken"/> 有值時以端點 Host 綁定並更新 hint。
    /// 端點不是有效網址時丟 <see cref="ArgumentException"/>；留空（尚未設定）允許。
    /// </summary>
    Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default);

    /// <summary>語音 Token 有沒有存在。只查存不存在、不讀內容。</summary>
    Task<bool> HasSpeechTokenAsync(CancellationToken ct = default);

    /// <summary>刪除語音 Token，並清空 hint 與綁定的主機。</summary>
    Task ClearSpeechTokenAsync(CancellationToken ct = default);

    /// <summary>
    /// 筆記資料夾（M10 10B）。空字串＝預設；不合法（非完整路徑、非法字元、環境變數不存在）丟 <see cref="ArgumentException"/>，訊息可直接顯示。
    /// 存了就通知 <see cref="Changed"/>，設定頁與筆記工具下一次讀取就用新的位置。
    /// </summary>
    Task SetNotesFolderAsync(string folder, CancellationToken ct = default);

    /// <summary>長期記憶的開關（M14）。存了就通知 <see cref="Changed"/>。</summary>
    Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default);

    /// <summary>是否在每則回答下方顯示 token 用量（進階）。預設實作不支援，只有 <c>JsonModelCatalog</c> 會存。</summary>
    Task SetShowTokenUsageAsync(bool show, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>工具清單頁的設定（M15）。存了就通知 <see cref="Changed"/>。</summary>
    Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default);

    /// <summary>
    /// 新增或更新一個 MCP 伺服器（M16）。<paramref name="newSecrets"/> 為 null＝祕密不變；有值就整份寫進 <see cref="ISecretStore"/>，並換新的 <see cref="McpServerSettings.SecretsVersion"/>。
    /// 代號不合法或重複丟 <see cref="ArgumentException"/>。存了就通知 <see cref="Changed"/>。
    /// </summary>
    Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default);

    /// <summary>刪除 MCP 伺服器，連同祕密、權限覆寫，以及工具設定裡屬於它的工具。</summary>
    Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default);

    /// <summary>讀出伺服器的環境變數與標頭的值；沒有時回 <see cref="McpServerSecrets.Empty"/>。</summary>
    Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default);

    /// <summary>
    /// 網路設定（M17）。先 <see cref="NetworkSettings.Normalize"/>，再用 <c>NetworkRules.Validate</c> 檢查，有錯丟 <see cref="ArgumentException"/>（訊息可直接顯示）。
    /// <paramref name="newProxyPassword"/> 有值＝寫入；null＝不變。Auth 不是 Account 時刪除已存的密碼。存了就通知 <see cref="Changed"/>。
    /// </summary>
    Task SetNetworkAsync(NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default);

    /// <summary>瀏覽器設定（M18）。先 <see cref="BrowserSettings.Normalize"/> 再存，存了就通知 <see cref="Changed"/>；設定頁的三項都是立即生效。</summary>
    Task SetBrowserAsync(BrowserSettings settings, CancellationToken ct = default);

    /// <summary>Proxy 密碼有沒有存在。只查存不存在、不讀內容。</summary>
    Task<bool> HasProxyPasswordAsync(CancellationToken ct = default);

    /// <summary>
    /// 連線最近一次測試或對話的結果（<see cref="ConnectionSettings.LastTest"/>），讓重開後狀態點仍看得到。
    /// 不通知 <see cref="Changed"/>；連線不存在時什麼都不做。預設實作不存（給測試用的假目錄）。
    /// </summary>
    Task SetConnectionTestAsync(string connectionId, ConnectionTestRecord? test, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>找不到（被刪了，或連線不見了）時回 null。</summary>
    ResolvedModel? Resolve(string? modelSettingsId);
}
