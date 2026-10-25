using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumi.Core.IO;
using Lumi.Core.Models;
using Lumi.Core.Mcp;
using Lumi.Core.Notes;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Settings;

/// <summary>
/// <see cref="IModelCatalog"/> 的實作：設定存在 <c>settings.json</c>（M2 的 Q1），金鑰只寫進 <see cref="ISecretStore"/>。
/// 建構時同步讀檔（檔案很小，只在啟動時發生一次）；寫入用 <see cref="AtomicFile"/>，同一程序內序列化。
/// </summary>
public sealed class JsonModelCatalog : IModelCatalog
{
    public const int CurrentVersion = 1;

    /// <summary>從 M1 搬家時建立的連線 Id：M1 的金鑰就存在 <c>connection/dev-anthropic/api-key</c>，沿用就不用搬金鑰。</summary>
    public const string MigratedConnectionId = "dev-anthropic";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly ISecretStore _secrets;
    private readonly ILogger<JsonModelCatalog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly bool _readOnly;
    private volatile AppSettings _current;

    public JsonModelCatalog(IOptions<ModelCatalogOptions> options, ISecretStore secrets, ILogger<JsonModelCatalog> logger)
    {
        _path = options.Value.ResolveSettingsPath();
        _secrets = secrets;
        _logger = logger;
        (_current, _readOnly, LoadProblem) = Load();
    }

    public AppSettings Current => _current;

    public string? LoadProblem { get; }

    public event EventHandler<AppSettings>? Changed;

    public async Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct = default)
    {
        ValidateConnection(connection);
        var secretKey = ConnectionSecrets.ApiKey(connection.Id);   // Id 格式不合法會在這裡丟 ArgumentException

        string? hint;
        if (newApiKey is not null)
        {
            await _secrets.SetAsync(secretKey, newApiKey, ct).ConfigureAwait(false);
            hint = ConnectionSecrets.Hint(newApiKey);
        }
        else
        {
            // 呼叫端傳進來的提示不採信，只有真的存了金鑰才會改
            hint = _current.Connections.FirstOrDefault(c => c.Id == connection.Id)?.ApiKeyHint;
        }

        var saved = connection with { ApiKeyHint = hint };
        await MutateAsync(s => s with { Connections = Upsert(s.Connections, saved, c => c.Id) }, ct).ConfigureAwait(false);
    }

    public Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct = default) =>
        _secrets.ExistsAsync(ConnectionSecrets.ApiKey(connectionId), ct);

    public async Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default)
    {
        RequireConnection(connectionId);
        await _secrets.DeleteAsync(ConnectionSecrets.ApiKey(connectionId), ct).ConfigureAwait(false);
        await MutateAsync(s => s with
        {
            Connections = s.Connections.Select(c => c.Id == connectionId ? c with { ApiKeyHint = null } : c).ToList(),
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default)
    {
        await MutateAsync(s => s with
        {
            Connections = s.Connections.Where(c => c.Id != connectionId).ToList(),
            Models = s.Models.Where(m => m.ConnectionId != connectionId).ToList(),
        }, ct).ConfigureAwait(false);

        try
        {
            await _secrets.DeleteAsync(ConnectionSecrets.ApiKey(connectionId), ct).ConfigureAwait(false);
        }
        catch (SecretStoreException ex)
        {
            // 設定已經刪了；留下一筆用不到的金鑰不影響功能，記下來就好
            _logger.LogWarning(ex, "刪除連線 {ConnectionId} 的金鑰失敗", connectionId);
        }
    }

    public Task SaveModelAsync(ModelSettings model, CancellationToken ct = default)
    {
        ValidateModel(model);
        RequireConnection(model.ConnectionId);
        return MutateAsync(s => s with { Models = Upsert(s.Models, model, m => m.Id) }, ct);
    }

    public Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default) =>
        MutateAsync(s => s with { Models = s.Models.Where(m => m.Id != modelSettingsId).ToList() }, ct);

    public Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default)
    {
        RequireModel(modelSettingsId);
        return MutateAsync(s => s with { WorkbenchModelId = modelSettingsId }, ct);
    }

    public Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default)
    {
        RequireModel(modelSettingsId);
        return MutateAsync(s => s with { SpriteModelId = modelSettingsId }, ct);
    }

    public Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default) =>
        MutateAsync(s => s with { Theme = theme }, ct);

    public Task SetThemeIdAsync(string themeId, CancellationToken ct = default)
    {
        var id = ThemePalettes.Normalize(themeId);
        return MutateAsync(s => s with { ThemeId = id }, ct);
    }

    public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default)
    {
        var normalized = persona.Normalize();
        if (normalized.CustomPersona is { Length: > PersonaSettings.CustomPersonaMaxLength })
            throw new ArgumentException($"自訂人設最多 {PersonaSettings.CustomPersonaMaxLength} 字。", nameof(persona));
        return MutateAsync(s => s with { Persona = normalized }, ct);
    }

    public Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct = default) =>
        MutateAsync(s => s with { Sprite = sprite.Normalize() }, ct);

    public Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct = default) =>
        MutateAsync(s => s with { WorkbenchWindow = window.Normalize() }, ct, notify: false);

    public Task SetNotesFolderAsync(string folder, CancellationToken ct = default)
    {
        var normalized = new NotesSettings(folder).Normalize();
        var problem = NoteFolderRules.Validate(normalized.Folder);
        if (problem is not null)
            throw new ArgumentException(problem, nameof(folder));
        return MutateAsync(s => s with { Notes = normalized }, ct);
    }

    // Phase 0 的最簡版本：只更新設定。祕密的讀寫與設定檔欄位由 M16 的 16B 補上
    public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default) =>
        MutateAsync(s => s with { McpServers = Upsert(s.McpServers, server, m => m.Id) }, ct);

    public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default) =>
        MutateAsync(s => s with { McpServers = s.McpServers.Where(m => m.Id != serverId).ToList() }, ct);

    public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) =>
        Task.FromResult(McpServerSecrets.Empty);

    public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default) =>
        MutateAsync(s => s with { Tools = tools.Normalize() }, ct);

    public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default) =>
        MutateAsync(s => s with { Memory = new MemorySettings(enabled).Normalize() }, ct);

    public async Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default)
    {
        var normalized = settings.Normalize();
        string? endpointHost = null;
        if (normalized.Endpoint.Length > 0)
        {
            var check = SpeechEndpointRules.Validate(normalized.Endpoint);
            if (!check.IsValid)
                throw new ArgumentException(check.Reason, nameof(settings));
            endpointHost = check.Uri!.Host;
        }

        // 呼叫端傳進來的 hint／主機不採信，只看這裡算出來的結果
        var current = _current.Speech;
        string? hint = null;
        string? tokenHost = null;
        if (newToken is not null)
        {
            if (endpointHost is null)
                throw new ArgumentException("請先輸入服務端點，再儲存 Token。", nameof(settings));
            await _secrets.SetAsync(SpeechSecrets.WhisperToken, newToken, ct).ConfigureAwait(false);
            hint = ConnectionSecrets.Hint(newToken);
            tokenHost = endpointHost;
        }
        else if (current.TokenHost is not null && string.Equals(current.TokenHost, endpointHost, StringComparison.OrdinalIgnoreCase))
        {
            hint = current.TokenHint;
            tokenHost = current.TokenHost;
        }
        else if (current.TokenHost is not null || current.TokenHint is not null)
        {
            // 端點主機變了（或端點被清空）：舊 Token 不能再送到新主機，直接刪掉
            await _secrets.DeleteAsync(SpeechSecrets.WhisperToken, ct).ConfigureAwait(false);
        }

        var saved = normalized with { TokenHint = hint, TokenHost = tokenHost };
        await MutateAsync(s => s with { Speech = saved }, ct).ConfigureAwait(false);
    }

    public Task<bool> HasSpeechTokenAsync(CancellationToken ct = default) =>
        _secrets.ExistsAsync(SpeechSecrets.WhisperToken, ct);

    public async Task ClearSpeechTokenAsync(CancellationToken ct = default)
    {
        await _secrets.DeleteAsync(SpeechSecrets.WhisperToken, ct).ConfigureAwait(false);
        await MutateAsync(s => s with { Speech = s.Speech with { TokenHint = null, TokenHost = null } }, ct).ConfigureAwait(false);
    }

    public ResolvedModel? Resolve(string? modelSettingsId)
    {
        var settings = _current;
        var model = settings.Models.FirstOrDefault(m => m.Id == modelSettingsId);
        var connection = model is null ? null : settings.Connections.FirstOrDefault(c => c.Id == model.ConnectionId);
        return connection is null ? null : new ResolvedModel(model!, connection);
    }

    /// <summary>第一次啟動（沒有設定檔）時的設定：沿用 M1 的開發設定。</summary>
    public static AppSettings CreateInitialSettings()
    {
        var connection = new ConnectionSettings(MigratedConnectionId, "Anthropic", ProviderFormat.Anthropic, "https://api.anthropic.com");
        var model = new ModelSettings(ModelSettings.NewId(), MigratedConnectionId, "claude-opus-5-5", "Claude Opus 5.5",
            MaxOutputTokens: 64_000, ContextWindow: 1_000_000, ShowThinking: true);
        return new AppSettings([connection], [model], model.Id, model.Id, ThemePreference.System);
    }

    /// <summary>
    /// 去掉指向不存在連線的模型；預設模型不存在時改成第一個模型（一個都沒有就是 null）。
    /// </summary>
    public static AppSettings Normalize(AppSettings settings)
    {
        var connectionIds = settings.Connections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var models = settings.Models.Where(m => connectionIds.Contains(m.ConnectionId)).ToList();

        string? Fix(string? id) => models.Any(m => m.Id == id) ? id : models.FirstOrDefault()?.Id;

        return settings with
        {
            Models = models,
            WorkbenchModelId = Fix(settings.WorkbenchModelId),
            SpriteModelId = Fix(settings.SpriteModelId),
            ThemeId = ThemePalettes.Normalize(settings.ThemeId),
            Speech = settings.Speech.Normalize(),
            WorkbenchWindow = settings.WorkbenchWindow?.Normalize(),
            Notes = settings.Notes.Normalize(),
            Memory = settings.Memory.Normalize(),
        };
    }

    private async Task MutateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct, bool notify = true)
    {
        if (_readOnly)
            throw new InvalidOperationException(LoadProblem ?? "設定檔目前無法寫入。");

        AppSettings next;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            next = Normalize(change(_current));
            await AtomicFile.WriteAllBytesAsync(_path, Serialize(next), ct).ConfigureAwait(false);
            _current = next;
        }
        finally
        {
            _writeLock.Release();
        }

        if (notify)
            Changed?.Invoke(this, next);
    }

    private (AppSettings Settings, bool ReadOnly, string? Problem) Load()
    {
        if (!File.Exists(_path))
        {
            var initial = CreateInitialSettings();
            WriteSync(initial);
            _logger.LogInformation("建立新的設定檔 {Path}", _path);
            return (initial, false, null);
        }

        SettingsFile? file;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 讀不進來不是損毀：不改名、不覆寫，讓使用者自己處理（M9 9C 的啟動失敗說明）
            throw new SettingsFileUnreadableException(_path, ex);
        }

        try
        {
            file = JsonSerializer.Deserialize<SettingsFile>(bytes, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "設定檔損毀");
            file = null;
        }

        if (file is null || file.Connections is null || file.Models is null)
            return RecoverFromBrokenFile();

        if (file.Version > CurrentVersion)
        {
            // 較新版的 Lumi 寫的：不能用，也不能覆蓋，只用預設值跑、不寫檔
            _logger.LogWarning("設定檔版本 {Version} 比程式新", file.Version);
            return (CreateInitialSettings(),
                true,
                $"設定檔是較新版本的 Lumi 建立的（版本 {file.Version}），這次先用預設設定，而且不會儲存任何設定變更。");
        }

        // Persona 是 M5 加的欄位：舊檔沒有時用預設值（版本號不變，舊版程式讀到會直接忽略這個欄位）
        var settings = Normalize(new AppSettings(file.Connections, file.Models, file.WorkbenchModelId, file.SpriteModelId, file.Theme)
        {
            Persona = file.Persona?.Normalize() ?? PersonaSettings.Default,
            Sprite = file.Sprite?.Normalize() ?? SpriteSettings.Default,   // M7 加的欄位，同樣舊檔沒有時用預設
            ThemeId = ThemePalettes.Normalize(file.ThemeId),               // 主題色票（UA）：缺欄位或不認得就是 sage
            WorkbenchWindow = ReadWorkbenchWindow(file.WorkbenchWindow),   // M9 9D 加的欄位，讀不進來只影響視窗位置
            Speech = file.Speech?.Normalize() ?? SpeechSettings.Default,   // M6 加的欄位，舊檔沒有時用預設
            Notes = ReadNotes(file.Notes),                                 // M10 10B 加的欄位，格式錯誤時當作預設
            Memory = ReadMemory(file.Memory),                              // M14 加的欄位，缺少或格式錯誤時當作預設（開）
        });
        return (settings, false, null);
    }

    /// <summary>
    /// 視窗位置是非關鍵資料：格式錯了就當作沒有（視窗照預設置中），不能因此把整個設定檔當成損毀而重設。
    /// </summary>
    private WindowSettings? ReadWorkbenchWindow(JsonElement? element)
    {
        if (element is not JsonElement el || el.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            return el.Deserialize<WindowSettings>(JsonOptions)?.Normalize();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "視窗位置設定格式錯誤，改用預設位置");
            return null;
        }
    }

    /// <summary>
    /// 筆記設定（M10 10B）：資料夾不合法不在這裡處理（<c>NoteFolderRules</c> 在讀取時退回預設並記 log），
    /// 格式錯了就當作沒有，不能因此把整個設定檔當成損毀。
    /// </summary>
    private NotesSettings ReadNotes(JsonElement? element)
    {
        if (element is not JsonElement el || el.ValueKind != JsonValueKind.Object)
            return NotesSettings.Default;
        try
        {
            return el.Deserialize<NotesSettings>(JsonOptions)?.Normalize() ?? NotesSettings.Default;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "筆記設定格式錯誤，改用預設資料夾");
            return NotesSettings.Default;
        }
    }

    /// <summary>長期記憶設定（M14）：格式錯了就當作預設，不能因此把整個設定檔當成損毀。</summary>
    private MemorySettings ReadMemory(JsonElement? element)
    {
        if (element is not JsonElement el || el.ValueKind != JsonValueKind.Object)
            return MemorySettings.Default;
        try
        {
            return el.Deserialize<MemorySettings>(JsonOptions)?.Normalize() ?? MemorySettings.Default;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "記憶設定格式錯誤，改用預設值");
            return MemorySettings.Default;
        }
    }

    private (AppSettings, bool, string?) RecoverFromBrokenFile()
    {
        var brokenPath = $"{_path}.broken-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        File.Move(_path, brokenPath, overwrite: true);
        var initial = CreateInitialSettings();
        WriteSync(initial);
        return (initial, false, $"設定檔損毀，已改用預設設定。原本的檔案保留為 {Path.GetFileName(brokenPath)}。");
    }

    private void WriteSync(AppSettings settings) =>
        AtomicFile.WriteAllBytesAsync(_path, Serialize(settings)).GetAwaiter().GetResult();

    private static byte[] Serialize(AppSettings settings) => JsonSerializer.SerializeToUtf8Bytes(
        new SettingsFile(CurrentVersion, settings.Connections, settings.Models, settings.WorkbenchModelId, settings.SpriteModelId, settings.Theme,
            settings.Persona, settings.Sprite, settings.ThemeId, settings.Speech,
            settings.WorkbenchWindow is null ? null : JsonSerializer.SerializeToElement(settings.WorkbenchWindow.Normalize(), JsonOptions),
            JsonSerializer.SerializeToElement(settings.Notes.Normalize(), JsonOptions),
            JsonSerializer.SerializeToElement(settings.Memory.Normalize(), JsonOptions)),
        JsonOptions);

    private static List<T> Upsert<T>(IReadOnlyList<T> items, T item, Func<T, string> id)
    {
        var list = items.ToList();
        var index = list.FindIndex(x => id(x) == id(item));
        if (index >= 0) list[index] = item; else list.Add(item);
        return list;
    }

    private void RequireConnection(string connectionId)
    {
        if (_current.Connections.All(c => c.Id != connectionId))
            throw new ArgumentException($"找不到連線 {connectionId}。", nameof(connectionId));
    }

    private void RequireModel(string modelSettingsId)
    {
        if (_current.Models.All(m => m.Id != modelSettingsId))
            throw new ArgumentException($"找不到模型設定 {modelSettingsId}。", nameof(modelSettingsId));
    }

    private static void ValidateConnection(ConnectionSettings connection)
    {
        if (string.IsNullOrWhiteSpace(connection.Name))
            throw new ArgumentException("連線名稱不可為空。", nameof(connection));
        if (!Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Base URL 必須是 http 或 https 的完整網址。", nameof(connection));
    }

    private static void ValidateModel(ModelSettings model)
    {
        if (string.IsNullOrWhiteSpace(model.ModelId))
            throw new ArgumentException("模型 ID 不可為空。", nameof(model));
        if (string.IsNullOrWhiteSpace(model.DisplayName))
            throw new ArgumentException("顯示名稱不可為空。", nameof(model));
        if (model.MaxOutputTokens <= 0 || model.ContextWindow <= 0)
            throw new ArgumentException("最大輸出與上下文上限必須大於 0。", nameof(model));
    }

    /// <summary>檔案格式。只放非機密的設定；金鑰在 <see cref="ISecretStore"/>。</summary>
    private sealed record SettingsFile(
        int Version,
        IReadOnlyList<ConnectionSettings>? Connections,
        IReadOnlyList<ModelSettings>? Models,
        string? WorkbenchModelId,
        string? SpriteModelId,
        ThemePreference Theme,
        PersonaSettings? Persona = null,
        SpriteSettings? Sprite = null,
        string? ThemeId = null,
        SpeechSettings? Speech = null,
        JsonElement? WorkbenchWindow = null,    // 原樣保留成 JsonElement，格式錯誤時不影響整個檔案的讀取
        JsonElement? Notes = null,
        JsonElement? Memory = null);
}
