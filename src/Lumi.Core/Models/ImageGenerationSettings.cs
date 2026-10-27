using System.Text.Json.Serialization;

namespace Lumi.Core.Models;

/// <summary>
/// 圖片生成的設定（22F）：用哪個連線與模型產生圖片、預設尺寸。<paramref name="ConnectionId"/> 或 <paramref name="ModelId"/>
/// 沒有值就是尚未設定（工具會回說明，不呼叫 API）。連線被刪除時 <see cref="JsonModelCatalog"/> 會把 ConnectionId 清成 null。
/// </summary>
public sealed record ImageGenerationSettings(string? ConnectionId, string? ModelId, string Size = "1024x1024")
{
    public const string DefaultSize = "1024x1024";

    /// <summary>可選的尺寸（gpt-image 系列支援的三種）。</summary>
    public static readonly IReadOnlyList<string> Sizes = ["1024x1024", "1536x1024", "1024x1536"];

    public static readonly ImageGenerationSettings Default = new(null, null);

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionId) && !string.IsNullOrWhiteSpace(ModelId);

    /// <summary>去掉前後空白，空字串變成 null；尺寸不認得時用預設。</summary>
    public ImageGenerationSettings Normalize() => new(
        string.IsNullOrWhiteSpace(ConnectionId) ? null : ConnectionId.Trim(),
        string.IsNullOrWhiteSpace(ModelId) ? null : ModelId.Trim(),
        Sizes.Contains(Size, StringComparer.Ordinal) ? Size : DefaultSize);
}
