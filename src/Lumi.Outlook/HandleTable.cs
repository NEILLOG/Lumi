namespace Lumi.Outlook;

/// <summary>
/// 信件與行程的短代號（m1、e1…）對照表：每則對話各自編號，只存在記憶體，每則對話最多 500 個（超過丟最舊的）。執行緒安全。
/// </summary>
internal sealed class HandleTable
{
    /// <summary>加入並回傳代號（prefix＋流水號）；同一則對話、同一個 EntryID 再加時回原本的代號。</summary>
    public string Add(string conversationId, char prefix, string entryId, string storeId) => throw new NotImplementedException(); // TODO(30A)

    public bool TryGet(string conversationId, string id, out (string EntryId, string StoreId) handle) => throw new NotImplementedException(); // TODO(30A)
}
