using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Tarot;

namespace Lumi.App.ViewModels;

/// <summary>
/// 回覆附件（ui-spec 3.1）：工具產生、要給使用者看的東西（塔羅牌陣、產生的圖片）。
/// 由工具卡片（<see cref="ToolCardViewModel.ReplyAttachments"/>）產生，畫在工具之後那則助理訊息的氣泡上方（<see cref="MessageViewModel.ReplyAttachments"/>）。
/// </summary>
public abstract class ReplyAttachmentViewModel(string callId) : ObservableObject
{
    /// <summary>產生這個附件的工具呼叫 id。</summary>
    public string CallId { get; } = callId;
}

/// <summary>塔羅牌陣附件（<c>tarot_draw</c> 的結果）。</summary>
public sealed class TarotAttachmentViewModel(string callId, TarotSpreadModel spread) : ReplyAttachmentViewModel(callId)
{
    public TarotSpreadModel Spread { get; } = spread;
}

/// <summary>
/// 圖片附件（<c>generate_image</c>、<c>edit_image</c>，一次呼叫一張）。產生中（<see cref="IsPending"/>）時只有尺寸，
/// 完成後換成檔案；檔案被移走或刪除時 <see cref="Exists"/> 為 false。
/// </summary>
public sealed partial class ImageAttachmentViewModel : ReplyAttachmentViewModel
{
    /// <summary>縮圖的最大寬、高（ui-spec 3.1）。</summary>
    public const double MaxThumbnail = 320;

    private ImageAttachmentViewModel(string callId, string? filePath, bool exists, bool isPending, int pixelWidth, int pixelHeight) : base(callId)
    {
        FilePath = filePath;
        Exists = exists;
        _isPending = isPending;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    /// <summary>完成的圖片；<paramref name="exists"/> 是檢查當下檔案在不在。</summary>
    public static ImageAttachmentViewModel ForFile(string callId, string filePath, bool exists, int pixelWidth = 1024, int pixelHeight = 1024) =>
        new(callId, filePath, exists, isPending: false, pixelWidth, pixelHeight);

    /// <summary>產生中的佔位：尺寸取自參數（例如 1536×1024），還沒有檔案。<paramref name="startedAt"/> 是開始產生（允許之後）的時間，秒數由它算。</summary>
    public static ImageAttachmentViewModel Pending(string callId, int pixelWidth, int pixelHeight, DateTimeOffset? startedAt = null) =>
        new(callId, null, exists: false, isPending: true, pixelWidth, pixelHeight) { StartedAt = startedAt };

    /// <summary>產生中開始的時間（佔位沒有開始時間時為 null，秒數只能手動設定）。</summary>
    public DateTimeOffset? StartedAt { get; private init; }

    public string? FilePath { get; }

    /// <summary>檔名（縮圖的 Tooltip、遺失佔位顯示）。</summary>
    public string FileName => FilePath is null ? "" : Path.GetFileName(FilePath);

    public bool Exists { get; }

    /// <summary>圖片的像素尺寸（用來算縮圖與佔位的比例）。</summary>
    public int PixelWidth { get; }

    public int PixelHeight { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsImage), nameof(IsMissing), nameof(StatusText))]
    private bool _isPending;

    /// <summary>產生中經過的秒數（佔位上的「已 N 秒」）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private int _elapsedSeconds;

    public bool ShowsImage => !IsPending && Exists;

    public bool IsMissing => !IsPending && !Exists;

    /// <summary>產生中才推進：依 <see cref="StartedAt"/> 與 <paramref name="now"/> 算出經過的整秒數（畫面每秒呼叫一次）。</summary>
    public void Tick(DateTimeOffset now)
    {
        if (!IsPending || StartedAt is not { } started)
            return;
        ElapsedSeconds = Math.Max(0, (int)Math.Floor((now - started).TotalSeconds));
    }

    public string ElapsedText => string.Format(CultureInfo.CurrentCulture, Strings.Image_GeneratingElapsedFormat, ElapsedSeconds);

    /// <summary>目前狀態文字：產生中、遺失或檔名（佔位的無障礙名稱、live region 的內容）。</summary>
    public string StatusText => IsPending ? Strings.Image_Generating : IsMissing ? Strings.ToolCard_ImageMissing : FileName;

    /// <summary>完成的縮圖的無障礙名稱（「圖片：檔名」）。</summary>
    public string AttachmentName => string.Format(CultureInfo.CurrentCulture, Strings.Image_AttachmentAutomationFormat, FileName);

    /// <summary>縮圖（或佔位）的寬高：等比縮進 320 × 320（1024×1024 → 320×320、1536×1024 → 320×213、1024×1536 → 213×320）。</summary>
    public double DisplayWidth => Fit().Width;

    public double DisplayHeight => Fit().Height;

    private (double Width, double Height) Fit()
    {
        if (PixelWidth <= 0 || PixelHeight <= 0)
            return (MaxThumbnail, MaxThumbnail);
        var scale = Math.Min(MaxThumbnail / PixelWidth, MaxThumbnail / PixelHeight);
        return (Math.Round(PixelWidth * scale), Math.Round(PixelHeight * scale));
    }
}
