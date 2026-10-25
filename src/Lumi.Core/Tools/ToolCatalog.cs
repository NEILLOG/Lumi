using Lumi.Core.Models;

namespace Lumi.Core.Tools;

/// <summary>
/// <see cref="IToolCatalog"/> 的預設實作：工具來自 <see cref="IToolRegistry"/>，啟用設定來自 <see cref="IModelCatalog"/>。
/// 不快取：每次呼叫都讀目前的設定；對話的快照由 <see cref="Chat.ConversationService"/> 負責（M15 D1）。
/// </summary>
public sealed class ToolCatalog(IToolRegistry registry, IModelCatalog settings) : IToolCatalog
{
    public Task<IReadOnlyList<ITool>> GetEnabledForNewConversationAsync(CancellationToken ct)
    {
        var disabled = settings.Current.Tools.Disabled.ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<ITool> enabled = GetAll()
            .Where(t => !t.Descriptor.CanDisable || !disabled.Contains(t.Descriptor.Name))
            .ToList();
        return Task.FromResult(enabled);
    }

    public IReadOnlyList<ITool> GetAll() => registry.GetAll();

    public ITool? Find(string name) => registry.Find(name);

    public IReadOnlyList<ToolCatalogGroup> ListGroups()
    {
        // 找不到群組的工具（例如拼錯或 M16 的 MCP 群組）放進「一般」
        var general = ToolGroups.Find(ToolGroups.General)!;
        return GetAll()
            .GroupBy(t => ToolGroups.Find(t.Descriptor.Group) ?? general)
            .OrderBy(g => g.Key.Order)
            .Select(g => new ToolCatalogGroup(g.Key, g.OrderBy(t => t.Descriptor.DisplayName, StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
