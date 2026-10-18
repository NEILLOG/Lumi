namespace Lumi.Tools;

public sealed class ToolsOptions
{
    /// <summary>
    /// 是否註冊示範用的 <c>save_note</c>／<c>delete_note</c>。它們只為了驗收權限確認流程（M3），
    /// M4 有真正的檔案工具後移除。設定檔對應 <c>Lumi:Tools:EnableDemoTools</c>。
    /// </summary>
    public bool EnableDemoTools { get; set; } = true;

    /// <summary>筆記資料夾；null 表示「文件」資料夾下的 <c>Lumi/notes</c>。測試用暫存路徑。</summary>
    public string? NotesDirectory { get; set; }

    public string ResolveNotesDirectory() =>
        NotesDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lumi", "notes");
}
