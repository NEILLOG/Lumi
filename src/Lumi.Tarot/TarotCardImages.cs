using System.Text.Json;
using TarotMcp.Core;

namespace Lumi.Tarot;

/// <summary>讀 TarotMcp.Core 內嵌的牌面圖片（CardAssets）。圖片不解出到磁碟（D6）。</summary>
public sealed class TarotCardImages : ITarotCardImages
{
    public static ITarotCardImages Default { get; } = new TarotCardImages();

    private readonly Lazy<TarotWindow?> _frameWindow = new(LoadFrameWindow);

    public Stream? OpenCard(string cardId) => CardAssets.OpenCardImage(cardId);

    public Stream? OpenFrame() => CardAssets.OpenFrame();

    public TarotWindow? FrameWindow => _frameWindow.Value;

    private static TarotWindow? LoadFrameWindow()
    {
        try
        {
            using var stream = CardAssets.OpenFrameInfo();
            return ParseFrameInfo(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 frame.json（像素）並換算成比例。格式不對、範圍超出圖外回 null，不丟例外。
    /// 格式：{"size":{"width","height"},"window":{"x","y","width","height"}}
    /// </summary>
    internal static TarotWindow? ParseFrameInfo(Stream? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!TryGetObject(root, "size", out var size) || !TryGetObject(root, "window", out var window))
            {
                return null;
            }

            if (!TryGetNumber(size, "width", out var imageW) || !TryGetNumber(size, "height", out var imageH)
                || !TryGetNumber(window, "x", out var x) || !TryGetNumber(window, "y", out var y)
                || !TryGetNumber(window, "width", out var w) || !TryGetNumber(window, "height", out var h))
            {
                return null;
            }

            if (imageW <= 0 || imageH <= 0 || w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > imageW || y + h > imageH)
            {
                return null;
            }

            return new TarotWindow(x / imageW, y / imageH, w / imageW, h / imageH);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out value)
               && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetNumber(JsonElement parent, string name, out double value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object
               && parent.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetDouble(out value);
    }
}
