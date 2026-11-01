namespace Lumi.Tarot;

/// <summary>牌面圖片的來源。正式環境讀 TarotMcp.Core 內嵌的資源；測試可換成假的。</summary>
public interface ITarotCardImages
{
    /// <summary>開啟牌的插畫（webp）；id 格式不對或沒有這張圖回 null。呼叫端負責關閉串流。</summary>
    Stream? OpenCard(string cardId);

    /// <summary>開啟共用的邊框範本（png，窗口已去背）；沒有回 null。呼叫端負責關閉串流。</summary>
    Stream? OpenFrame();

    /// <summary>邊框窗口（比例）；沒有 frame.json 或格式錯誤回 null，畫面改用 <see cref="TarotWindow.Default"/>。</summary>
    TarotWindow? FrameWindow { get; }
}

/// <summary>邊框窗口，以邊框圖的比例表示（0～1，左上為原點）。</summary>
public sealed record TarotWindow(double Left, double Top, double Width, double Height)
{
    /// <summary>沒有窗口資料時的預設值：寬 90%、高 5%～83%。</summary>
    public static TarotWindow Default { get; } = new(0.05, 0.05, 0.90, 0.78);

    /// <summary>窗口中心由上往下的比例，逆位插畫的旋轉中心。</summary>
    public double CenterY => Top + Height / 2;
}
