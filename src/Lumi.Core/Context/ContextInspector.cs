namespace Lumi.Core.Context;

public enum ContextItemProblem
{
    None,

    /// <summary>檔案或資料夾不存在（可能被搬走或刪除）。</summary>
    Missing,

    /// <summary>不是文字檔：不會放進內容，模型也讀不了。</summary>
    NotText,

    /// <summary>文字檔太大，不嵌進訊息；仍在範圍內，模型用 <c>read_file</c> 分段讀。</summary>
    TooLarge,

    UnsupportedImage,

    ImageTooLarge,
}

/// <param name="EntryCount">資料夾裡的項目數（略過 <c>.git</c>、<c>node_modules</c> 等）。</param>
/// <param name="EntryCountCapped">項目數超過計數上限，實際更多。</param>
public sealed record ContextItemStatus(ContextItemProblem Problem, long? SizeBytes = null, int? EntryCount = null, bool EntryCountCapped = false)
{
    public bool HasProblem => Problem != ContextItemProblem.None;
}

/// <summary>
/// 檢查使用者加入的路徑：是檔案、資料夾還是圖片，以及目前的狀態（給 chip 顯示用）。
/// 要碰檔案系統，所以實作在 Lumi.Tools；ViewModel 不直接存取檔案。檢查不丟例外。
/// </summary>
public interface IContextInspector
{
    /// <summary>判斷種類：資料夾、圖片（依檔案開頭的位元組）、其他檔案；路徑不存在回 null。</summary>
    Task<ContextKind?> ClassifyAsync(string path, CancellationToken ct = default);

    Task<ContextItemStatus> InspectAsync(ContextKind kind, string path, CancellationToken ct = default);
}

public sealed class NullContextInspector : IContextInspector
{
    public Task<ContextKind?> ClassifyAsync(string path, CancellationToken ct = default) => Task.FromResult<ContextKind?>(null);

    public Task<ContextItemStatus> InspectAsync(ContextKind kind, string path, CancellationToken ct = default) =>
        Task.FromResult(new ContextItemStatus(ContextItemProblem.Missing));
}
