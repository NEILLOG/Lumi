using System.ComponentModel.DataAnnotations;

namespace Lumi.Providers.Anthropic;

/// <summary>
/// M1 開發用的 Anthropic 連線設定，寫在 <c>appsettings.json</c> 的 <c>Lumi:Dev:Anthropic</c>，
/// 只放非機密值；金鑰只在 <see cref="Lumi.Core.Secrets.ISecretStore"/>。M2 由 <c>IModelCatalog</c> 取代。
/// </summary>
public sealed class AnthropicDevOptions
{
    public const string SectionPath = "Lumi:Dev:Anthropic";

    /// <summary>固定用這個 <c>connection</c> id 去 <see cref="Lumi.Core.Secrets.ISecretStore"/> 取金鑰；M2 起改用連線自己的 id。</summary>
    public const string ConnectionId = "dev-anthropic";

    /// <summary>金鑰在 <see cref="Lumi.Core.Secrets.ISecretStore"/> 裡的名稱；1D 的金鑰對話框也寫到這裡。</summary>
    public static readonly Lumi.Core.Secrets.SecretKey ApiKeySecret = new($"connection/{ConnectionId}/api-key");

    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    [Required]
    public string Model { get; set; } = "claude-opus-5-5";

    [Range(1, 128_000)]
    public int MaxTokens { get; set; } = 64_000;

    /// <summary>1C 不用；1D 的狀態列用來顯示「上下文 14K / 200K」。</summary>
    [Range(1, int.MaxValue)]
    public int ContextWindow { get; set; } = 1_000_000;

    /// <summary><c>low</c>／<c>medium</c>／<c>high</c>／<c>xhigh</c>／<c>max</c>；null 表示不送，由模型用自己的預設值。</summary>
    public string? Effort { get; set; }

    /// <summary>
    /// 開啟後，模型的安全分類器拒絕回答時由 API 在同一次呼叫內改用其他模型（Q9）。
    /// 需要用 beta 端點；回應可能多出 fallback 區塊，1C 的 <c>ProviderBlock</c> 會原樣保留。
    /// </summary>
    public bool UseServerFallbacks { get; set; } = true;
}
