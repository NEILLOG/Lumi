using System.ComponentModel;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Tools.Memory;

public sealed record MemoryDeleteArgs(
    [property: Description("要忘掉的記憶代號，例如 m3（system 的 <lumi_memory> 裡列出的 [m3]）；也接受 3")] string Id);

/// <summary>
/// 刪除（忘掉）一則記憶（M14 14C）。<c>Destructive</c>：每次都要使用者確認，不提供「本次對話都允許」。
/// 刪除後不再送給模型，無法復原。
/// </summary>
public sealed class MemoryDeleteTool(IMemoryStore memory, IModelCatalog catalog) : ToolBase<MemoryDeleteArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "memory_delete", "忘掉",
        "刪除使用者要求遺忘的一則指定記憶（例如「忘掉…」「不要再記得…」）。必須提供既有記憶的代號 id，例如 m3。"
        + "只在使用者明確要求時使用。每次呼叫都需要使用者確認。"
        + "刪除後要照實告訴使用者刪掉了哪一則；被拒絕或失敗時也要照實說，不要假裝已經忘掉。",
        ToolPermission.Destructive, ToolModes.Both, TimeSpan.FromSeconds(10), RequiresScope: false, RequiresMemory: true, Group: ToolGroups.Memory, Summary: "刪除一則記憶。");

    protected override ToolPreview Describe(MemoryDeleteArgs args, ToolContext context)
    {
        const string detail = "忘掉之後不再送給模型供應商，無法復原";
        if (!MemoryRules.TryParseId(args.Id, out var id))
            return new ToolPreview(null, detail);

        var original = memory.TryGet(id);
        var target = original is null
            ? MemoryRules.FormatId(id)
            : $"{MemoryRules.FormatId(id)} · {MemoryToolSupport.Clip(original.Content, MemoryToolSupport.TargetChars)}";
        return new ToolPreview(target, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(MemoryDeleteArgs args, ToolContext context, CancellationToken ct)
    {
        if (!MemoryToolSupport.IsEnabled(catalog))
            return MemoryToolSupport.Disabled;

        if (!MemoryRules.TryParseId(args.Id, out var id))
            return ToolResult.Error("id 無效，請用像 m3 的代號。", "id 無效");

        var idText = MemoryRules.FormatId(id);
        if (!await memory.DeleteAsync(id, ct))
            return ToolResult.Error($"找不到記憶 {idText}。", "找不到");

        return ToolResult.Ok($"已忘掉 {idText}。", $"已忘掉 {idText}");
    }
}
