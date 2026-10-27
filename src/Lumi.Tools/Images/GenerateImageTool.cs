using System.ComponentModel;
using Lumi.Core.Images;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Tools.Images;

public sealed record GenerateImageArgs(
    [property: Description("要畫的內容，盡量具體（主體、風格、構圖、色彩）；英文或使用者的語言都可以")] string Prompt,
    [property: Description("尺寸：1024x1024（正方形）、1536x1024（橫向）、1024x1536（直向）；沒填就用設定裡的預設尺寸")] string? Size = null);

/// <summary>用設定的圖片模型產生一張圖，存到「圖片／Lumi」（22F）。圖片不送回模型，結果只有存檔路徑。</summary>
public sealed class GenerateImageTool(IModelCatalog catalog, IImageGenerator generator, GeneratedImageStore store) : ToolBase<GenerateImageArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "generate_image", "產生圖片",
        "用設定的圖片模型畫一張新圖，存到電腦的「圖片／Lumi」資料夾，結果只會給你存檔路徑。" +
        "只有使用者要求畫圖或產生圖片時才呼叫；會產生 API 費用。要修改既有的圖片請用 edit_image。",
        ToolPermission.Network, ToolModes.Workbench, TimeSpan.FromSeconds(240),
        Group: ToolGroups.Images, Summary: "用設定的模型產生圖片，存到圖片資料夾。");

    protected override ToolPreview Describe(GenerateImageArgs args, ToolContext context) =>
        new(null, $"{ImageToolSupport.PromptPreview(args.Prompt ?? "")}\n尺寸：{ImageToolSupport.SizeForPreview(args.Size, catalog.Current.ImageGeneration)}");

    protected override async Task<ToolResult> InvokeAsync(GenerateImageArgs args, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(args.Prompt))
            return ToolResult.Error("缺少 prompt（要畫的內容）。", "參數不正確");
        if (!ImageToolSupport.TryResolveConnection(catalog, out var connection, out var modelId, out var error))
            return error!;
        if (!ImageToolSupport.TryResolveSize(args.Size, catalog.Current.ImageGeneration, out var size, out error))
            return error!;

        GeneratedImage image;
        try
        {
            image = await generator.GenerateAsync(new ImageGenerationRequest(connection, modelId, args.Prompt.Trim(), size), ct).ConfigureAwait(false);
        }
        catch (ImageGenerationException ex)
        {
            return ToolResult.Error(ex.Message, "生成失敗");
        }

        return await ImageToolSupport.SaveResultAsync(store, image, context, edited: false, size, ct).ConfigureAwait(false);
    }
}
