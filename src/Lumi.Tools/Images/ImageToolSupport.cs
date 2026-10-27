using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Images;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.Tools.Images;

/// <summary>圖片工具共用（22F）：設定檢查、尺寸、來源圖片的解析、存檔與結果。</summary>
internal static class ImageToolSupport
{
    public const int MaxSources = 4;
    public const int MaxSourceBytes = 20 * 1024 * 1024;
    public const int PreviewPromptChars = 120;

    private const string NotConfiguredMessage = "還沒有設定圖片生成。請使用者到「設定 › AI 連線 › 圖片生成」選連線與模型後，再請求一次。";
    private const string SizeMessage = "尺寸只能是 1024x1024、1536x1024 或 1024x1536。";

    private static readonly string[] AcceptedMediaTypes = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>找出設定的連線與模型；沒設定或連線已被刪除時回說明。</summary>
    public static bool TryResolveConnection(IModelCatalog catalog, out ConnectionSettings connection, out string modelId, out ToolResult? error)
    {
        var settings = catalog.Current.ImageGeneration;
        var found = settings.IsConfigured ? catalog.Current.Connections.FirstOrDefault(c => c.Id == settings.ConnectionId) : null;
        if (found is null)
        {
            connection = null!;
            modelId = "";
            error = ToolResult.Error(NotConfiguredMessage, "尚未設定");
            return false;
        }

        connection = found;
        modelId = settings.ModelId!;
        error = null;
        return true;
    }

    /// <summary>沒填尺寸用設定的預設；填了就必須是三種之一。</summary>
    public static bool TryResolveSize(string? requested, ImageGenerationSettings settings, out string size, out ToolResult? error)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            size = settings.Size;
            error = null;
            return true;
        }

        size = requested.Trim();
        if (ImageGenerationSettings.Sizes.Contains(size, StringComparer.Ordinal))
        {
            error = null;
            return true;
        }
        error = ToolResult.Error(SizeMessage, "參數不正確");
        return false;
    }

    /// <summary>尺寸的預覽文字：不合法時照原樣顯示（執行時才回錯誤）。</summary>
    public static string SizeForPreview(string? requested, ImageGenerationSettings settings) =>
        string.IsNullOrWhiteSpace(requested) ? settings.Size : requested.Trim();

    public static string PromptPreview(string prompt)
    {
        var text = prompt.Trim();
        return text.Length <= PreviewPromptChars ? text : text[..PreviewPromptChars] + "…";
    }

    /// <summary>存檔並組成結果：內容是存檔路徑與尺寸（給模型），<see cref="ToolResult.ImageFiles"/> 給畫面顯示。</summary>
    public static async Task<ToolResult> SaveResultAsync(
        GeneratedImageStore store, GeneratedImage image, ToolContext context, bool edited, string size, CancellationToken ct)
    {
        string path;
        try
        {
            path = await store.SaveAsync(image, context.Time.GetLocalNow(), edited, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ToolResult.Error("無法儲存圖片，請檢查「圖片」資料夾的權限。", "無法儲存");
        }

        var content = $"已{(edited ? "編輯" : "產生")}圖片，存在 {path}（尺寸 {size}）。";
        if (!string.IsNullOrWhiteSpace(image.RevisedPrompt))
            content += $"\n供應商改寫後的提示：{image.RevisedPrompt}";
        content += "\n圖片不會顯示給你看。要再修改這張圖，用 edit_image 並把上面的路徑放進 images。";
        return ToolResult.Ok(content, edited ? "已編輯圖片" : "已產生圖片") with { ImageFiles = [path] };
    }

    /// <summary>
    /// 解析編輯的來源圖片，依序：(a) 這則對話使用者附上的圖片（用顯示名稱）；(b) 這則對話先前產生或編輯的圖片（完整路徑）；
    /// (c) 使用者加入範圍內的圖片檔。其他一律拒絕，不能讀任意檔案。只接受 PNG、JPEG、WebP，每張最多 20 MB。
    /// </summary>
    public static async Task<(List<SourceImage>? Sources, ToolResult? Error)> ResolveSourcesAsync(
        IReadOnlyList<string> references, ToolContext context, IConversationStore conversations, IAttachmentStore attachments, CancellationToken ct)
    {
        var messages = await conversations.GetMessagesAsync(context.ConversationId, ct).ConfigureAwait(false);
        var attached = messages.Where(m => m.Role == ChatRole.User).SelectMany(m => m.Content.OfType<ImageBlock>()).ToList();
        var generated = messages.Where(m => m.Role == ChatRole.Tool)
            .SelectMany(m => m.Content.OfType<ToolResultBlock>())
            .SelectMany(r => r.ImageFiles ?? [])
            .ToList();

        var sources = new List<SourceImage>();
        foreach (var raw in references)
        {
            var reference = raw.Trim();
            ToolResult? error;

            if (attached.LastOrDefault(b => string.Equals(b.DisplayName, reference, StringComparison.OrdinalIgnoreCase)) is { } block)
            {
                var data = await attachments.ReadAsync(block.FileName, ct).ConfigureAwait(false);
                if (data is null)
                    return (null, ToolResult.Error($"附上的圖片「{block.DisplayName}」已經遺失，不能當作來源。", "找不到來源圖片"));
                error = AddSource(sources, data, block.DisplayName);
            }
            else if (generated.FirstOrDefault(p => IsSamePath(p, reference)) is { } produced)
            {
                error = await ReadAndAddAsync(sources, produced, ct).ConfigureAwait(false);
            }
            else
            {
                var resolved = context.Scope.Resolve(reference);
                if (!resolved.IsAllowed || !File.Exists(resolved.FullPath!))
                {
                    var detail = resolved.IsAllowed ? "" : resolved.Error;
                    return (null, ToolResult.Error(
                        $"找不到來源圖片「{reference}」。來源只能是：使用者在這則對話附上的圖片名稱、這則對話先前產生或編輯的圖片路徑，或加入範圍內的圖片檔。{detail}",
                        "找不到來源圖片"));
                }
                error = await ReadAndAddAsync(sources, resolved.FullPath!, ct).ConfigureAwait(false);
            }

            if (error is not null)
                return (null, error);
        }
        return (sources, null);
    }

    private static async Task<ToolResult?> ReadAndAddAsync(List<SourceImage> sources, string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxSourceBytes)
            return ToolResult.Error($"來源圖片「{info.Name}」超過 20 MB。", "圖片太大");
        return AddSource(sources, await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false), info.Name);
    }

    /// <summary>檢查格式與大小後加入；不合格回說明。</summary>
    private static ToolResult? AddSource(List<SourceImage> sources, byte[] data, string displayName)
    {
        if (data.LongLength > MaxSourceBytes)
            return ToolResult.Error($"來源圖片「{displayName}」超過 20 MB。", "圖片太大");

        var mediaType = ImageFormats.Detect(data);
        if (mediaType is null || !AcceptedMediaTypes.Contains(mediaType, StringComparer.Ordinal))
            return ToolResult.Error($"來源圖片「{displayName}」不是 PNG、JPEG 或 WebP，不能用來編輯。", "格式不支援");

        // multipart 的檔名要帶副檔名，依實際格式重組
        var stem = Path.GetFileNameWithoutExtension(displayName);
        var fileName = $"{(string.IsNullOrWhiteSpace(stem) ? "image" : stem)}.{ImageFormats.Extension(mediaType)}";
        sources.Add(new SourceImage(data, mediaType, fileName));
        return null;
    }

    private static bool IsSamePath(string stored, string reference)
    {
        try
        {
            var comparison = FileScopeRules.ForCurrentOs.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(stored), Path.GetFullPath(reference), comparison);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
