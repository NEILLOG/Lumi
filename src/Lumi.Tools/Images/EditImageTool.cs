using System.ComponentModel;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Images;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Tools.Images;

public sealed record EditImageArgs(
    [property: Description("要怎麼改這些圖片，例如「把背景換成海邊」「把兩張圖合成一張」；英文或使用者的語言都可以")] string Prompt,
    [property: Description("來源圖片，1 到 4 個。每個填以下其中一種：使用者在這則對話附上的圖片名稱（例如 照片.jpg）、這則對話先前產生或編輯的圖片完整路徑、或使用者加入範圍內的圖片檔路徑")] IReadOnlyList<string> Images,
    [property: Description("尺寸：1024x1024、1536x1024、1024x1536；沒填就用設定裡的預設尺寸")] string? Size = null);

/// <summary>用設定的圖片模型編輯或合成 1 到 4 張圖，存成新檔（22F）。來源只能是對話裡的圖片或範圍內的圖片檔。</summary>
public sealed class EditImageTool(
    IModelCatalog catalog, IImageGenerator generator, GeneratedImageStore store, IConversationStore conversations, IAttachmentStore attachments)
    : ToolBase<EditImageArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "edit_image", "編輯圖片",
        "用設定的圖片模型修改或合成 1 到 4 張圖（換背景、改風格、合併等），結果存成新檔到「圖片／Lumi」資料夾，只會給你存檔路徑。" +
        "只有使用者要求修改、合成、換背景等圖片處理時才呼叫；來源圖片只能是這則對話附上的圖、這則對話先前產生的圖，或使用者加入範圍內的圖片檔。會產生 API 費用。",
        ToolPermission.Network, ToolModes.Workbench, TimeSpan.FromSeconds(240),
        Group: ToolGroups.Images, Summary: "用設定的模型編輯或合成圖片，存成新檔。");

    protected override ToolPreview Describe(EditImageArgs args, ToolContext context)
    {
        var sources = (args.Images ?? []).Select(Label).ToList();
        var detail = $"來源：{string.Join("、", sources)}\n{ImageToolSupport.PromptPreview(args.Prompt ?? "")}\n尺寸：{ImageToolSupport.SizeForPreview(args.Size, catalog.Current.ImageGeneration)}";
        return new ToolPreview(null, detail);
    }

    protected override async Task<ToolResult> InvokeAsync(EditImageArgs args, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Prompt))
            return ToolResult.Error("缺少 prompt（要怎麼改）。", "參數不正確");
        if (args.Images is not { Count: >= 1 and <= ImageToolSupport.MaxSources })
            return ToolResult.Error($"來源圖片需要 1 到 {ImageToolSupport.MaxSources} 張。", "參數不正確");
        if (!ImageToolSupport.TryResolveConnection(catalog, out var connection, out var modelId, out var error))
            return error!;
        if (!ImageToolSupport.TryResolveSize(args.Size, catalog.Current.ImageGeneration, out var size, out error))
            return error!;

        var (sources, sourceError) = await ImageToolSupport.ResolveSourcesAsync(args.Images, context, conversations, attachments, ct).ConfigureAwait(false);
        if (sourceError is not null)
            return sourceError;

        GeneratedImage image;
        try
        {
            image = await generator.EditAsync(
                new ImageEditRequest(connection, modelId, args.Prompt.Trim(), size, sources!), ct).ConfigureAwait(false);
        }
        catch (ImageGenerationException ex)
        {
            return ToolResult.Error(ex.Message, "編輯失敗");
        }

        return await ImageToolSupport.SaveResultAsync(store, image, context, edited: true, size, ct).ConfigureAwait(false);
    }

    /// <summary>卡片與確認區顯示的來源名稱：路徑只顯示檔名。</summary>
    private static string Label(string reference)
    {
        var text = reference.Trim();
        try
        {
            return Path.IsPathRooted(text) ? Path.GetFileName(text) : text;
        }
        catch (ArgumentException)
        {
            return text;
        }
    }
}
