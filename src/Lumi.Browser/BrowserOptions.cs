namespace Lumi.Browser;

/// <summary>瀏覽器功能的路徑設定。由 Lumi.App 的組裝設定（使用者資料夾下的 <c>browser</c>）。</summary>
public sealed class BrowserOptions
{
    /// <summary>Lumi 專屬的瀏覽器設定檔的根資料夾；Edge 與 Chrome 各用底下的 <c>edge</c>、<c>chrome</c>。</summary>
    public string ProfileRoot { get; set; } = "";
}
