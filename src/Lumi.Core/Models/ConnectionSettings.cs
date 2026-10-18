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
public sealed record ModelSettings(
    string Id,
    string ConnectionId,
    string ModelId,
    string DisplayName,
    int MaxOutputTokens,
    int ContextWindow,
    bool ShowThinking)
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
}

/// <summary>一次請求要用的完整資訊。</summary>
public sealed record ResolvedModel(ModelSettings Model, ConnectionSettings Connection);
