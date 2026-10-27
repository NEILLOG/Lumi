using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.Core.Images;

/// <summary>
/// 圖片生成與編輯（22F）。實作在 Lumi.Providers（OpenAI 相容的 <c>/images/generations</c> 與 <c>/images/edits</c>）。
/// 失敗一律丟 <see cref="ImageGenerationException"/>，訊息可以直接給模型看。
/// </summary>
public interface IImageGenerator
{
    Task<GeneratedImage> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default);

    Task<GeneratedImage> EditAsync(ImageEditRequest request, CancellationToken ct = default);
}

public sealed record ImageGenerationRequest(ConnectionSettings Connection, string ModelId, string Prompt, string Size);

public sealed record ImageEditRequest(
    ConnectionSettings Connection, string ModelId, string Prompt, string Size, IReadOnlyList<SourceImage> Images);

/// <summary>編輯用的來源圖片。<paramref name="FileName"/> 只用來當 multipart 的檔名，要帶副檔名。</summary>
public sealed record SourceImage(byte[] Data, string MediaType, string FileName);

/// <summary><paramref name="RevisedPrompt"/>：供應商改寫過的提示（有的話才有值）。</summary>
public sealed record GeneratedImage(byte[] Data, string MediaType, string? RevisedPrompt);

/// <summary>圖片生成失敗。<paramref name="Kind"/> 沿用對話的失敗分類（金鑰錯、網路、速率限制…）。</summary>
/// <summary>沒有註冊圖片生成服務時（測試或精簡的組裝）：每次都回說明，不呼叫任何 API。</summary>
public sealed class NullImageGenerator : IImageGenerator
{
    public Task<GeneratedImage> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default) =>
        throw new ImageGenerationException(ChatFailureKind.Unknown, "沒有可用的圖片生成服務。");

    public Task<GeneratedImage> EditAsync(ImageEditRequest request, CancellationToken ct = default) =>
        throw new ImageGenerationException(ChatFailureKind.Unknown, "沒有可用的圖片生成服務。");
}

public sealed class ImageGenerationException(ChatFailureKind kind, string message) : Exception(message)
{
    public ChatFailureKind Kind { get; } = kind;
}
