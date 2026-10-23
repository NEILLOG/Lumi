namespace Lumi.Tools;

public sealed class ToolsOptions
{
    /// <summary>檔案類工具（<c>list_dir</c>、<c>read_file</c>、<c>search</c>，以及寫入類 <c>write_file</c> 等）的上限。</summary>
    public FileToolOptions Files { get; set; } = new();
}
