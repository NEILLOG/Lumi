using System.ComponentModel;
using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Tools.Memory;

public sealed record MemorySaveArgs(
    [property: Description("要記住的內容，一句話、客觀陳述（1～200 字）")] string Content,
    [property: Description("分類：profile（本人）、preference（偏好）、work（工作）、project（專案）；不分大小寫")] string Category,
    [property: Description("選填到期日，格式 yyyy-MM-dd（例如 2026-12-31），到期後不再送給模型。省略就是永久")] string? ExpiresAt = null);

/// <summary>
/// 記住一則長期記憶（M14 14C）。<c>Write</c>：每次都要使用者確認，不做觸發詞免確認（寫進記憶＝永久性的提示注入）。
/// <c>Source</c> 由程式判定：最新一則使用者訊息含觸發詞是 <see cref="MemorySource.Explicit"/>，否則是 <see cref="MemorySource.Suggested"/>；
/// 模型在引數裡宣告的來源一律忽略（參數沒有 <c>source</c>）。
/// </summary>
public sealed class MemorySaveTool(IMemoryStore memory, IModelCatalog catalog, IConversationStore conversations, MemoryOptions options) : ToolBase<MemorySaveArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "memory_save", "記住",
        "把使用者長期不變的個人資訊（身分、偏好、工作職責、專案背景）記進長期記憶，之後每次對話都會自動帶上。"
        + "每則一句話、客觀陳述、200 字內。只在使用者明確要求記住時使用（例如「記住…」「別忘了…」「以後…」），"
        + "或這明顯是長期有用的事實；後者要先問使用者是否要記，不要直接記。"
        + "某一天發生的事請用 add_note 記筆記；要在某個時間點提醒請用 create_reminder。"
        + "不要記密碼、金鑰、卡號等機密。與既有記憶衝突時改用 memory_update，不要再新增一則。"
        + "每次呼叫都需要使用者確認；被拒絕就不要再重試同一則內容。",
        ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(10), RequiresScope: false, RequiresMemory: true,
        Group: ToolGroups.Memory, Summary: "記住關於你的長期事實。", CanAlwaysAllow: false);

    protected override ToolPreview Describe(MemorySaveArgs args, ToolContext context)
    {
        var content = MemoryRules.NormalizeContent(args.Content);
        var parts = new List<string>();
        if (MemoryRules.TryParseCategory(args.Category, out var category))
            parts.Add($"分類：{MemoryRules.CategoryName(category)}");
        if (!string.IsNullOrWhiteSpace(args.ExpiresAt) && MemoryRules.TryParseDate(args.ExpiresAt, out var expires))
            parts.Add($"有效至 {MemoryToolSupport.Date(expires)}");
        parts.Add(MemoryToolSupport.SendsToProviderNote);

        var target = content.Length == 0 ? null : MemoryToolSupport.Clip(content, MemoryToolSupport.TargetChars);
        return new ToolPreview(target, string.Join(" · ", parts));
    }

    protected override async Task<ToolResult> InvokeAsync(MemorySaveArgs args, ToolContext context, CancellationToken ct)
    {
        if (!MemoryToolSupport.IsEnabled(catalog))
            return MemoryToolSupport.Disabled;

        var content = MemoryRules.NormalizeContent(args.Content);
        if (content.Length == 0)
            return ToolResult.Error("content 不能為空。", "缺少 content");
        if (!MemoryRules.TryParseCategory(args.Category, out var category))
            return ToolResult.Error("category 必須是 profile、preference、work 或 project。", "分類不正確");

        DateOnly? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(args.ExpiresAt))
        {
            if (!MemoryRules.TryParseDate(args.ExpiresAt, out var date))
                return ToolResult.Error("expires_at 必須是 YYYY-MM-DD 格式。", "日期不正確");
            var today = DateOnly.FromDateTime(context.Time.GetLocalNow().DateTime);
            if (date < today)
                return ToolResult.Error("到期日不能早於今天。", "日期不正確");
            expiresAt = date;
        }

        var source = await DetermineSourceAsync(context, ct);
        var result = await memory.AddAsync(new NewMemory(content, category, source, context.ConversationId, expiresAt), ct);

        switch (result.Outcome)
        {
            case MemoryAddOutcome.Added when result.Item is { } added:
                var id = MemoryRules.FormatId(added.Id);
                return ToolResult.Ok(
                    $"已記住 {id}：{added.Content}（{MemoryRules.CategoryName(added.Category)}）。",
                    $"已記住 {id}");

            case MemoryAddOutcome.Duplicate:
                // 不是錯誤：內容已經在記憶裡了
                var message = result.Item is { } dup
                    ? $"已經有相同的記憶 {MemoryRules.FormatId(dup.Id)}，不需要重複記錄。"
                    : "已經有相同的記憶，不需要重複記錄。";
                return ToolResult.Ok(message, "已有相同記憶");

            case MemoryAddOutcome.TooManyItems:
            case MemoryAddOutcome.TooManyCharacters:
                return ToolResult.Error(await MemoryToolSupport.CapacityMessageAsync(memory, options, ct), "記憶已滿");

            default:
                return ToolResult.Error(result.Message ?? "這則內容不符合記憶的規則。", "無法記住");
        }
    }

    /// <summary>
    /// 來源由程式判定：最新一則使用者訊息（略過工具結果）含觸發詞就是 <see cref="MemorySource.Explicit"/>。
    /// 讀不到訊息也是 <see cref="MemorySource.Suggested"/>（較嚴格的那一邊）。
    /// </summary>
    private async Task<MemorySource> DetermineSourceAsync(ToolContext context, CancellationToken ct)
    {
        try
        {
            var messages = await conversations.GetMessagesAsync(context.ConversationId, ct);
            var latest = messages.LastOrDefault(m => m.Role == ChatRole.User);
            if (latest is null)
                return MemorySource.Suggested;

            var text = string.Concat(latest.Content.OfType<TextBlock>().Select(b => b.Text));
            return MemoryRules.ContainsTriggerWord(text, options.TriggerWords) ? MemorySource.Explicit : MemorySource.Suggested;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return MemorySource.Suggested;
        }
    }
}
