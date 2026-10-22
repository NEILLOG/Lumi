using Lumi.Core.Speech;

namespace Lumi.Core.Models;

public enum ProviderFormat { Anthropic, OpenAICompatible }

/// <summary>
/// 一組連線：供應商格式、Base URL。金鑰不在這裡，只在 <see cref="Secrets.ISecretStore"/>
/// （名稱見 <see cref="ConnectionSecrets"/>）；<paramref name="ApiKeyHint"/> 只是末 4 碼，給設定頁顯示「已設定 · 結尾 xxxx」。
/// </summary>
/// <param name="Id">小寫英數與 -，會用在金鑰名稱裡。</param>
public sealed record ConnectionSettings(
    string Id,
    string Name,
    ProviderFormat Format,
    string BaseUrl,
    string? ApiKeyHint = null)
{
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>掛在某個連線底下的模型設定。</summary>
/// <param name="ModelId">送給供應商的模型 ID，例如 <c>claude-opus-5-5</c>。</param>
/// <param name="ShowThinking">關掉時不要求思考內容（也適用於不支援 adaptive thinking 的舊模型）。</param>
/// <param name="ToolsEnabled">關掉時送出的請求完全不帶工具定義（不支援工具的模型，例如某些小型本機模型）。預設開；舊設定檔沒有這個欄位時也是開。</param>
public sealed record ModelSettings(
    string Id,
    string ConnectionId,
    string ModelId,
    string DisplayName,
    int MaxOutputTokens,
    int ContextWindow,
    bool ShowThinking,
    bool ToolsEnabled = true)
{
    public static string NewId() => Guid.NewGuid().ToString("N");
}

public enum ThemePreference { System, Light, Dark }

/// <summary>
/// 所有使用者設定的快照（不可變）。工作台與小精靈各自的預設模型存 <see cref="ModelSettings.Id"/>；
/// 目前「工作台用哪個模型」就是 <see cref="WorkbenchModelId"/>（M2 的 Q3）。
/// </summary>
public sealed record AppSettings(
    IReadOnlyList<ConnectionSettings> Connections,
    IReadOnlyList<ModelSettings> Models,
    string? WorkbenchModelId,
    string? SpriteModelId,
    ThemePreference Theme)
{
    public static readonly AppSettings Empty = new([], [], null, null, ThemePreference.System);

    /// <summary>回答風格（M5）。不放在建構式參數裡：舊設定檔、既有呼叫端都不需要改，沒有時就是預設值。</summary>
    public PersonaSettings Persona { get; init; } = PersonaSettings.Default;

    /// <summary>小精靈的角色、尺寸、位置與上次的模式（M7）。同樣是後來加的欄位，舊檔沒有時用預設值。</summary>
    public SpriteSettings Sprite { get; init; } = SpriteSettings.Default;

    /// <summary>主題色票 ID（ui-guide.html 第 1 節，見 <see cref="ThemePalettes"/>）。舊檔沒有時是預設的 sage。</summary>
    public string ThemeId { get; init; } = ThemePalettes.Default;

    /// <summary>語音服務的端點、語言、逾時與 Token 的 hint／綁定主機（M6）。舊檔沒有時用預設值，Token 本身不在這裡。</summary>
    public SpeechSettings Speech { get; init; } = SpeechSettings.Default;
}

/// <summary>一次請求要用的完整資訊。</summary>
public sealed record ResolvedModel(ModelSettings Model, ConnectionSettings Connection);
