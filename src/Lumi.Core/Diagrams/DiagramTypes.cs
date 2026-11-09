namespace Lumi.Core.Diagrams;

/// <summary>畫好的圖：PNG（2 倍解析度）與它在畫面上的大小（邏輯像素）。</summary>
public sealed record DiagramImage(byte[] Png, double Width, double Height);

/// <summary>畫圖的結果：成功有 <see cref="Image"/>；失敗時 <see cref="Problem"/> 是給使用者看的原因。</summary>
public sealed record DiagramRenderResult(DiagramImage? Image, string? Problem)
{
    public bool Succeeded => Image is not null;

    public static DiagramRenderResult Ok(DiagramImage image) => new(image, null);

    public static DiagramRenderResult Fail(string problem) => new(null, problem);
}

/// <summary>
/// 把回覆裡的 Mermaid 程式碼畫成圖片。實作在 Lumi.Browser：用電腦上已安裝的 Edge／Chrome 在背景離線執行內建的 mermaid.js，
/// 圖的內容不會送到網路上。不丟例外（取消除外），失敗回 <see cref="DiagramRenderResult.Fail"/>。
/// </summary>
public interface IDiagramRenderer
{
    Task<DiagramRenderResult> RenderMermaidAsync(string code, bool dark, CancellationToken ct = default);
}

/// <summary>沒有畫圖功能（測試與預設）：畫面改顯示程式碼。</summary>
public sealed class NullDiagramRenderer : IDiagramRenderer
{
    public const string Problem = "這個環境沒有畫圖的功能。";

    public Task<DiagramRenderResult> RenderMermaidAsync(string code, bool dark, CancellationToken ct = default) =>
        Task.FromResult(DiagramRenderResult.Fail(Problem));
}
