using Lumi.Core.Mcp;
using Lumi.Core.Models;

namespace Lumi.Core.Tools;

/// <summary>
/// <see cref="IToolCatalog"/> 的預設實作：內建工具來自 <see cref="IToolRegistry"/>，MCP 工具來自 <see cref="IMcpToolSource"/>，
/// 啟用設定來自 <see cref="IModelCatalog"/>。
/// 不快取：每次呼叫都讀目前的設定；對話的快照由 <see cref="Chat.ConversationService"/> 負責（M15 D1）。
/// </summary>
public sealed class ToolCatalog(IToolRegistry registry, IModelCatalog settings, IMcpToolSource? mcp = null) : IToolCatalog
{
    private readonly IMcpToolSource _mcp = mcp ?? new EmptyMcpToolSource();

    public async Task<IReadOnlyList<ITool>> GetEnabledForNewConversationAsync(CancellationToken ct)
    {
        // 先等還在連線中的伺服器（M16 D9，最多 10 秒）；快取的 MCP 工具不進新對話（M16 D12）
        await _mcp.WaitForStartupAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        var disabled = settings.Current.Tools.Disabled.ToHashSet(StringComparer.Ordinal);
        // 預設關閉的工具（M31 D13）：只有使用者在 Enabled 開啟才納入；Disabled 對它們沒有作用
        var enabledOptIn = settings.Current.Tools.Enabled.ToHashSet(StringComparer.Ordinal);
        return registry.GetAll()
            .Concat(_mcp.GetTools().Where(t => t.IsLive).Cast<ITool>())
            .Where(t => t.Descriptor.OptInWarning is null
                ? !t.Descriptor.CanDisable || !disabled.Contains(t.Descriptor.Name)
                : enabledOptIn.Contains(t.Descriptor.Name))
            // 這台電腦用不了的工具：新對話不帶定義；已經在快照裡的不在這裡處理（呼叫時由工具回錯誤）
            .Where(t => AvailabilityOf(t) == ToolAvailabilityKind.Available)
            .OrderBy(t => t.Descriptor.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>只回內建工具（M16：MCP 工具不進這裡，舊對話補快照不能把它們加進去）。</summary>
    public IReadOnlyList<ITool> GetAll() => registry.GetAll();

    public ITool? Find(string name) =>
        registry.Find(name) ?? _mcp.GetTools().FirstOrDefault(t => t.Descriptor.Name == name);

    public IReadOnlyList<ToolCatalogGroup> ListGroups()
    {
        // 找不到群組的內建工具放進「一般」
        var general = ToolGroups.Find(ToolGroups.General)!;
        // 內建群組裡全部工具都是「不在這台電腦上」的，整組不列；只有部分或「不可用」的照常列出，讓清單頁顯示原因
        var builtIn = GetAll()
            .GroupBy(t => ToolGroups.Find(t.Descriptor.Group) ?? general)
            .Select(g => new ToolCatalogGroup(g.Key, g.ToList()))
            .Where(g => !(g.Tools.Count > 0 && g.Tools.All(t => AvailabilityOf(t) == ToolAvailabilityKind.NotOnThisPlatform)));

        // 每個已啟用的伺服器一組（即使沒有工具，畫面要顯示「還沒有取得過…」）；依名稱排序，排在內建之後
        var servers = settings.Current.McpServers
            .Where(s => s.Enabled)
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select((s, i) => (Server: s, Group: new ToolGroupInfo(McpToolGroup(s.Id), s.Name, 100 + i)))
            .ToList();
        var mcpTools = _mcp.GetTools().ToLookup(t => t.Descriptor.Group, StringComparer.Ordinal);
        var mcpGroups = servers.Select(s => new ToolCatalogGroup(s.Group, mcpTools[s.Group.Id].Cast<ITool>().ToList()));

        return builtIn
            .OrderBy(g => g.Group.Order)
            .Concat(mcpGroups)
            .Select(g => g with { Tools = g.Tools.OrderBy(t => t.Descriptor.DisplayName, StringComparer.Ordinal).ToList() })
            .ToList();
    }

    /// <summary>實作 <see cref="IConditionalTool"/> 的工具看自己的查詢結果；其他工具一律可用。</summary>
    private static ToolAvailabilityKind AvailabilityOf(ITool tool) =>
        tool is IConditionalTool conditional ? conditional.CheckAvailability().Kind : ToolAvailabilityKind.Available;

    /// <summary>MCP 工具的群組代號（<see cref="ToolDescriptor.Group"/>）。</summary>
    private static string McpToolGroup(string serverId) => "mcp:" + serverId;
}
