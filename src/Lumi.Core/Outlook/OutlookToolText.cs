namespace Lumi.Core.Outlook;

/// <summary>Outlook 工具共用的文字。</summary>
public static class OutlookToolText
{
    /// <summary>在工具清單頁開啟 Outlook 工具前要確認的說明（整組工具共用同一段，只問一次）。</summary>
    public const string OptInWarning =
        "開啟後，Lumi 可以使用這台電腦的 classic Outlook：搜尋與讀取你的信件、查看行事曆、把附件存到對話的資料夾、建立信件與會議邀請的草稿、寄信。\n\n" +
        "• 讀到的信件與行程內容會送給你選用的 AI 模型供應商（和你在對話裡貼上的內容一樣）。不能送出公司信件的話，請不要開啟。\n" +
        "• 信件與會議邀請預設建成草稿，在 Outlook 打開，由你檢查後自己按「傳送」。\n" +
        "• Lumi 只有兩種情況會直接寄出：寄到你自己的信箱（主旨會有 [Lumi]），或是你在確認卡上看過收件人與內文、按了「允許執行」的那一封。\n" +
        "• 信件裡可能藏著要 Lumi 做事的文字。對話讀過信件之後，建立草稿或寄出時確認卡會提醒你。\n\n" +
        "只支援 Windows 的 classic Outlook，而且 Outlook 要先開著。";

    /// <summary>這台電腦沒有 classic Outlook 時，工具清單頁顯示的原因。</summary>
    public const string NotInstalled = "這台電腦沒有 classic Outlook。New Outlook 與網頁版不支援這組工具。";

    /// <summary>Outlook 沒有開著時回給模型的訊息。</summary>
    public const string NotRunning = "請先開啟 Outlook 再試一次。";
}
