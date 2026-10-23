namespace Lumi.Core.Notes;

/// <summary>
/// 筆記的存取（M10 10B）。每天一個 Markdown 檔，只追加、不改寫。檔名一律由日期產生，
/// 呼叫端給的字串不會變成路徑。資料夾由 <see cref="INoteFolderProvider"/> 每次操作時決定，設定改了立即生效。
/// 寫入或讀取失敗時丟 <see cref="NoteStoreException"/>，訊息可以直接給使用者看。
/// </summary>
public interface INoteStore
{
    /// <summary>目前的筆記資料夾（給工具結果與設定頁顯示用）。</summary>
    string FolderPath { get; }

    /// <summary>
    /// 在 <paramref name="date"/> 的檔案末尾追加一則。檔案不存在時建立並寫入標題行。
    /// 同一程序內的追加依序進行，不會交錯。
    /// </summary>
    Task AppendAsync(DateOnly date, TimeOnly time, string? category, string content, CancellationToken ct);

    /// <summary>
    /// 讀取 <paramref name="from"/> 到 <paramref name="to"/>（含兩端）之間的筆記，依日期由舊到新。
    /// <paramref name="keyword"/> 不為空時只回傳內容或分類含此字串（不分大小寫）的記事；沒有記事的日期不回傳。
    /// </summary>
    Task<IReadOnlyList<NoteDay>> ReadAsync(DateOnly from, DateOnly to, string? keyword, CancellationToken ct);
}
