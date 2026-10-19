namespace Lumi.Infrastructure.Attachments;

public sealed class AttachmentOptions
{
    /// <summary>附件資料夾；null 表示 <see cref="LumiPaths.DataDirectory"/> 下的 <c>attachments</c>。測試用暫存路徑。</summary>
    public string? Directory { get; set; }

    public string ResolveDirectory() => Directory ?? Path.Combine(LumiPaths.DataDirectory, "attachments");
}
