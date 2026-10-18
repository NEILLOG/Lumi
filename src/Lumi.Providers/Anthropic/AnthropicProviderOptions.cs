namespace Lumi.Providers.Anthropic;

/// <summary>
/// Anthropic Provider 本身的行為設定（不是連線；連線與模型在 <see cref="Lumi.Core.Models.IModelCatalog"/>）。
/// 預設值就是正式行為；<c>appsettings.json</c> 的 <c>Lumi:Providers:Anthropic</c> 可以覆寫，平常不需要。
/// </summary>
public sealed class AnthropicProviderOptions
{
    public const string SectionPath = "Lumi:Providers:Anthropic";

    /// <summary><c>low</c>～<c>max</c>；null 表示不送，由模型用自己的預設值。</summary>
    public string? Effort { get; set; }

    /// <summary>模型的安全分類器拒絕回答時，由 API 在同一次呼叫內改用候補模型（M1 的 Q9）。</summary>
    public bool UseServerFallbacks { get; set; } = true;
}
