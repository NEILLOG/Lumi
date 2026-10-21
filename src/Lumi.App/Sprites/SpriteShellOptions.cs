namespace Lumi.App.Sprites;

/// <summary>
/// 殼的各種數字。沿用 M0 POC 在 Mac 與 Windows 驗證過的值，集中在這裡方便之後調整。
/// 尺寸類的數字（DIP）都不隨檔位變；靠近半徑例外，會隨光球直徑等比縮放。
/// </summary>
public sealed class SpriteShellOptions
{
    /// <summary>直徑 104 時，游標距光球中心多近算「靠近」。</summary>
    public double NearRadiusAt104 { get; init; } = 150;

    /// <summary>游標離開後延遲多久才算離開，避免閃爍。</summary>
    public TimeSpan NearHold { get; init; } = TimeSpan.FromSeconds(0.6);

    /// <summary>長按多久算「戳」。</summary>
    public TimeSpan LongPress { get; init; } = TimeSpan.FromSeconds(0.45);

    /// <summary>位移超過多少算「拖曳」（DIP）。</summary>
    public double DragThreshold { get; init; } = 4;

    /// <summary>待機且沒人靠近多久進入休眠。</summary>
    public TimeSpan AutoSleep { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>輪詢游標的間隔（也是長按與靠近判定的時間解析度）。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(33);

    // ---- 對話整合（7D） ----

    /// <summary><c>Done</c> 停多久回到 <c>Idle</c>。</summary>
    public TimeSpan DoneHold { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>回答完成後，氣泡停留多久才淡出（游標停在上面時暫停計時）。</summary>
    public TimeSpan BubbleLinger { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 氣泡最多顯示幾個字形叢集。比 prompt 的「約 60 字」寬一倍，是第三層（硬性）上限：
    /// 超過就在叢集邊界截斷並加「…」，同時出現「在工作台開啟完整內容 →」。只影響氣泡，存檔與工作台都是完整的。
    /// </summary>
    public int BubbleMaxClusters { get; init; } = 120;

    /// <summary>「暫時隱藏」多久。</summary>
    public TimeSpan TemporaryHide { get; init; } = TimeSpan.FromMinutes(30);

    // ---- 版面（DIP） ----

    /// <summary>視窗最小寬度：容納氣泡（280）與輸入列（300）。</summary>
    public double MinWindowWidth { get; init; } = 340;

    /// <summary>角色上方預留給氣泡的高度。固定預留，不隨內容改視窗大小（避免拖曳與動畫時視窗一直變形）。</summary>
    public double BubbleReserve { get; init; } = 260;

    public double InputBarHeight { get; init; } = 48;

    /// <summary>輸入列與角色框之間的間距。</summary>
    public double InputGap { get; init; } = 6;

    /// <summary>輸入列下方留的邊（陰影）。</summary>
    public double BottomMargin { get; init; } = 12;

    // ---- 幀率 ----

    public int ActiveFps { get; init; } = 60;

    public int CalmFps { get; init; } = 30;

    public int SleepFps { get; init; } = 12;
}
