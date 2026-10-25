using System.ComponentModel;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Tools.Memory;

public sealed record MemoryUpdateArgs(
    [property: Description("要修改的記憶代號，例如 m3（system 的 <lumi_memory> 裡列出的 [m3]）；也接受 3")] string Id,
    [property: Description("新的內容，一句話（1～200 字）；省略就不改")] string? Content = null,
    [property: Description("新的分類：profile、preference、work、project（不分大小寫）；省略就不改")] string? Category = null,
    [property: Description("新的到期日，格式 yyyy-MM-dd；給 never 就清除到期日（改成永久）。省略就不改")] string? ExpiresAt = null);

/// <summary>
/// 修改既有記憶的內容、分類或到期日（M14 14C）。<c>Destructive</c>：會丟掉原本的內容，所以每次都要使用者確認、不提供「本次對話都允許」。
/// 已過期的記憶仍可修改（用來延長到期日）。
/// </summary>
public sealed class MemoryUpdateTool(IMemoryStore memory, IModelCatalog catalog, MemoryOptions options) : ToolBase<MemoryUpdateArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "memory_update", "修改記憶",
        "修改一則既有記憶的內容、分類或到期日（例如使用者換了部門、偏好改變）。"
        + "必須提供既有記憶的代號 id，例如 m3（system 的 <lumi_memory> 裡列出的 [m3]）。"
        + "只改有給的欄位；expires_at 給 never 就清除到期日。要新增一則記憶請用 memory_save，不要用這個工具。"
        + "每次呼叫都需要使用者確認；被拒絕就照實告訴使用者，不要假裝已經改好。",
        ToolPermission.Destructive, ToolModes.Both, TimeSpan.FromSeconds(10), RequiresScope: false, RequiresMemory: true, Group: ToolGroups.Memory, Summary: "修改一則記憶。");

    protected override ToolPreview Describe(MemoryUpdateArgs args, ToolContext context)
    {
        if (!MemoryRules.TryParseId(args.Id, out var id))
            return new ToolPreview(null, MemoryToolSupport.SendsToProviderNote);

        var original = memory.TryGet(id);
        var newContent = args.Content is null ? null : MemoryRules.NormalizeContent(args.Content);
        var shown = string.IsNullOrEmpty(newContent) ? original?.Content : newContent;
        var target = shown is null
            ? MemoryRules.FormatId(id)
            : $"{MemoryRules.FormatId(id)} · {MemoryToolSupport.Clip(shown, MemoryToolSupport.TargetChars)}";

        // 只列有變的部分；原本與之後並列。找不到原本（已被刪）就只顯示「之後」。
        var mainBefore = new List<string>();
        var mainAfter = new List<string>();
        if (!string.IsNullOrEmpty(newContent) && (original is null || newContent != original.Content))
        {
            if (original is not null)
                mainBefore.Add($"「{original.Content}」");
            mainAfter.Add($"「{newContent}」");
        }
        if (args.Category is not null && MemoryRules.TryParseCategory(args.Category, out var category)
            && (original is null || category != original.Category))
        {
            if (original is not null)
                mainBefore.Add($"（{MemoryRules.CategoryName(original.Category)}）");
            mainAfter.Add($"（{MemoryRules.CategoryName(category)}）");
        }

        string? expiryBefore = null;
        string? expiryAfter = null;
        if (args.ExpiresAt is not null)
        {
            DateOnly? newExpiry = null;
            var provided = true;
            if (IsNever(args.ExpiresAt))
                newExpiry = null;
            else if (MemoryRules.TryParseDate(args.ExpiresAt, out var date))
                newExpiry = date;
            else
                provided = false;

            if (provided && (original is null || original.ExpiresAt != newExpiry))
            {
                expiryAfter = ExpiryText(newExpiry);
                if (original is not null)
                    expiryBefore = ExpiryText(original.ExpiresAt);
            }
        }

        var afterText = string.Join(" · ", new[] { string.Concat(mainAfter), expiryAfter }.Where(s => !string.IsNullOrEmpty(s)));
        var beforeText = string.Join(" · ", new[] { string.Concat(mainBefore), expiryBefore }.Where(s => !string.IsNullOrEmpty(s)));
        var change = afterText.Length == 0
            ? "沒有變更"
            : original is null
                ? $"之後：{afterText}"
                : $"原本：{beforeText} → 之後：{afterText}";

        return new ToolPreview(target, $"{change} · {MemoryToolSupport.SendsToProviderNote}");
    }

    protected override async Task<ToolResult> InvokeAsync(MemoryUpdateArgs args, ToolContext context, CancellationToken ct)
    {
        if (!MemoryToolSupport.IsEnabled(catalog))
            return MemoryToolSupport.Disabled;

        if (!MemoryRules.TryParseId(args.Id, out var id))
            return ToolResult.Error("id 無效，請用像 m3 的代號。", "id 無效");
        if (args.Content is null && args.Category is null && args.ExpiresAt is null)
            return ToolResult.Error("至少要提供 content、category、expires_at 其中一項。", "沒有要修改的欄位");

        string? content = null;
        if (args.Content is not null)
            content = MemoryRules.NormalizeContent(args.Content);

        MemoryCategory? category = null;
        if (args.Category is not null)
        {
            if (!MemoryRules.TryParseCategory(args.Category, out var parsed))
                return ToolResult.Error("category 必須是 profile、preference、work 或 project。", "分類不正確");
            category = parsed;
        }

        DateOnly? expiresAt = null;
        var clearExpiresAt = false;
        if (args.ExpiresAt is not null)
        {
            if (IsNever(args.ExpiresAt))
            {
                clearExpiresAt = true;
            }
            else
            {
                if (!MemoryRules.TryParseDate(args.ExpiresAt, out var date))
                    return ToolResult.Error("expires_at 必須是 YYYY-MM-DD 格式，或 never（清除到期日）。", "日期不正確");
                var today = DateOnly.FromDateTime(context.Time.GetLocalNow().DateTime);
                if (date < today)
                    return ToolResult.Error("到期日不能早於今天。", "日期不正確");
                expiresAt = date;
            }
        }

        var patch = new MemoryPatch(
            Content: content,
            Category: category,
            ExpiresAt: expiresAt,
            ClearExpiresAt: clearExpiresAt,
            SkipSecretCheck: false);
        var result = await memory.UpdateAsync(id, patch, ct);
        var idText = MemoryRules.FormatId(id);

        switch (result.Outcome)
        {
            case MemoryUpdateOutcome.Updated when result.Item is { } updated:
                return ToolResult.Ok(
                    $"已更新 {idText}：{updated.Content}（{MemoryRules.CategoryName(updated.Category)}）。",
                    $"已更新 {idText}");

            case MemoryUpdateOutcome.Updated:
                return ToolResult.Ok($"已更新 {idText}。", $"已更新 {idText}");

            case MemoryUpdateOutcome.NotFound:
                return ToolResult.Error($"找不到記憶 {idText}。", "找不到");

            case MemoryUpdateOutcome.Duplicate:
                var message = result.Item is { } dup
                    ? $"已經有內容相同的記憶 {MemoryRules.FormatId(dup.Id)}。"
                    : "已經有內容相同的記憶。";
                return ToolResult.Error(message, "內容重複");

            case MemoryUpdateOutcome.TooManyCharacters:
                return ToolResult.Error(await MemoryToolSupport.CapacityMessageAsync(memory, options, ct), "記憶已滿");

            default:
                return ToolResult.Error(result.Message ?? "這則內容不符合記憶的規則。", "無法修改");
        }
    }

    private static bool IsNever(string value) => string.Equals(value.Trim(), "never", StringComparison.OrdinalIgnoreCase);

    private static string ExpiryText(DateOnly? expiresAt) =>
        expiresAt is { } date ? $"有效至 {MemoryToolSupport.Date(date)}" : "永久";
}
