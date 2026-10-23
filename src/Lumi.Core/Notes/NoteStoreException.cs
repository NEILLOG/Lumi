namespace Lumi.Core.Notes;

/// <summary>筆記讀寫失敗。<see cref="Exception.Message"/> 是給使用者看的繁體中文，不含路徑或例外細節。</summary>
public sealed class NoteStoreException(string userMessage) : Exception(userMessage)
{
    public string UserMessage => Message;
}
