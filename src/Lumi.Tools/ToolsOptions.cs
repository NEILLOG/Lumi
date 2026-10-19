namespace Lumi.Tools;

public sealed class ToolsOptions
{
    /// <summary>
    /// 是否註冊示範用的 <c>save_note</c>／<c>delete_note</c>。它們只為了驗收權限確認流程（M3），
    /// M10 有真正的寫入類工具後移除。預設關。設定檔對應 <c>Lumi:Tools:EnableDemoTools</c>。
    /// </summary>
    public bool EnableDemoTools { get; set; }

    /// <summary>檔案類工具（<c>list_dir</c>、<c>read_file</c>、<c>search</c>）的上限。</summary>
    public FileToolOptions Files { get; set; } = new();

    /// <summary>筆記資料夾；null 表示「文件」資料夾下的 <c>Lumi/notes</c>。測試用暫存路徑。</summary>
    public string? NotesDirectory { get; set; }

    public string ResolveNotesDirectory() =>
        NotesDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Lumi", "notes");
}
