namespace Lumi.Core.Commands;

/// <summary>執行指令工具的固定文字（M31）。</summary>
public static class CommandToolText
{
    /// <summary>在工具清單頁開啟 <c>run_command</c> 前要確認的警告（M31 設計 7）。</summary>
    public const string OptInWarning =
        "開啟後，Lumi 可以在你的電腦上執行 PowerShell、cmd 或 bash 指令，例如安裝 npm 套件、查詢電腦狀態。\n\n" +
        "• 每次執行前都會讓你看完整指令，按「允許執行」才會執行。看不懂的指令請按「拒絕」。\n" +
        "• 這不是沙盒：指令能做到你自己在終端機能做的任何事，包括刪除你的檔案、把資料傳到網路上。Lumi 會擋掉少數明顯危險的寫法，但擋不住全部。\n" +
        "• 網頁、信件或外部工具的內容可能藏著要 Lumi 執行指令的文字。對話讀過這類內容時，確認卡會提醒你。\n\n" +
        "只建議熟悉指令的人開啟。";
}
