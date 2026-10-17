namespace Lumi.Core.Chat;

/// <summary>
/// 目前使用的模型設定。M1 由 App 從 <c>appsettings.json</c> 的 <c>Lumi:Dev:Anthropic</c> 組出來；
/// M2 由 <c>IModelCatalog</c> 取代。
/// </summary>
public sealed record ChatDefaults(string ModelId, int MaxTokens, int ContextWindow);
