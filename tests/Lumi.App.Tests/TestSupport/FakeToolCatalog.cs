using System.Text.Json.Nodes;
using Lumi.Core.Tools;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 設定頁「工具」用的假目錄：群組依 <see cref="ToolGroups.BuiltIn"/> 的順序排，沒有工具的群組不回傳（和 ToolCatalog 的規則一致）。
/// 不讀設定，也不執行工具；啟用與確認方式的行為由 <see cref="ToolsViewModel"/> 與 <c>IModelCatalog</c> 負責。
/// </summary>
internal sealed class FakeToolCatalog : IToolCatalog
{
    private readonly IReadOnlyList<ITool> _tools;

    public FakeToolCatalog(params ITool[] tools)
    {
        _tools = tools.OrderBy(t => t.Descriptor.Name, StringComparer.Ordinal).ToList();
    }

    public Task<IReadOnlyList<ITool>> GetEnabledForNewConversationAsync(CancellationToken ct) => Task.FromResult(GetAll());

    public IReadOnlyList<ITool> GetAll() => _tools;

    public ITool? Find(string name) => _tools.FirstOrDefault(t => t.Descriptor.Name == name);

    public IReadOnlyList<ToolCatalogGroup> ListGroups() =>
    [
        .. ToolGroups.BuiltIn
            .OrderBy(g => g.Order)
            .Select(g => new ToolCatalogGroup(g, _tools
                .Where(t => t.Descriptor.Group == g.Id)
                .OrderBy(t => t.Descriptor.DisplayName, StringComparer.Ordinal)
                .ToList()))
            .Where(g => g.Tools.Count > 0),
    ];
}

/// <summary>可設定描述（群組、說明、能不能關、能不能永遠允許）的假工具。執行只回固定結果。</summary>
internal sealed class FakeCatalogTool : ITool
{
    public FakeCatalogTool(ToolDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public ToolDescriptor Descriptor { get; }

    public JsonNode InputSchema { get; } = new JsonObject { ["type"] = "object" };

    public ToolPreview Preview(string argumentsJson, ToolContext context) => ToolPreview.None;

    public Task<ToolResult> ExecuteAsync(string argumentsJson, ToolContext context, CancellationToken ct) =>
        Task.FromResult(ToolResult.Ok("假結果"));

    public static FakeCatalogTool Make(string name, string displayName, ToolPermission permission = ToolPermission.ReadOnly,
        string group = ToolGroups.General, string? summary = "測試用的說明。", bool canDisable = true, bool canAlwaysAllow = true,
        ToolModes modes = ToolModes.Both) =>
        new(new ToolDescriptor(name, displayName, "測試用工具。", permission, modes, TimeSpan.FromSeconds(5),
            Group: group, Summary: summary, CanDisable: canDisable, CanAlwaysAllow: canAlwaysAllow));
}
