namespace Lumi.Core.Usage;

public sealed record DownscaledImage(byte[] Data, string MediaType);

/// <summary>加入上下文的大圖在存成附件前縮小（L5）。實作在 App（SkiaSharp），Tools 只依賴這個介面。</summary>
public interface IImageDownscaler
{
    /// <summary>長邊超過 <paramref name="maxEdge"/> 就等比縮小，回新的位元組與媒體類型；不需要縮或無法處理時回 null（呼叫端用原圖）。</summary>
    DownscaledImage? Downscale(byte[] data, string mediaType, int maxEdge);
}
