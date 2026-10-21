using Lumi.Core.Chat;

namespace Lumi.Core.Models;

/// <summary>小精靈的尺寸檔位：光球直徑 64／80／104（<c>SpriteLimits.Tiers</c>）。</summary>
public enum SpriteSize { Small, Medium, Large }

/// <summary>角色中心在螢幕上的位置（螢幕像素，虛擬桌面座標，多螢幕時可以是負值）。</summary>
public sealed record SpritePlacement(int CenterX, int CenterY);

/// <summary>
/// 小精靈的設定（M7）。<paramref name="Placement"/> 是 null 表示還沒拖動過，放主螢幕右下角；
/// <paramref name="LastMode"/> 是上次結束時的模式，啟動時回到它。
/// </summary>
public sealed record SpriteSettings(string SpriteId, SpriteSize Size, bool VoiceEnabled, SpritePlacement? Placement, ChatMode LastMode)
{
    /// <summary>光球的 id（<c>OrbSpriteFactory.SpriteId</c>）；Core 不引用角色專案，所以在這裡寫一次。</summary>
    public const string DefaultSpriteId = "lumi.orb";

    public static readonly SpriteSettings Default = new(DefaultSpriteId, SpriteSize.Medium, true, null, ChatMode.Workbench);

    /// <summary>讀進來的值不可信（手動改檔、舊版寫的）：不合法的欄位退回預設。</summary>
    public SpriteSettings Normalize() => new(
        string.IsNullOrWhiteSpace(SpriteId) ? DefaultSpriteId : SpriteId.Trim(),
        Enum.IsDefined(Size) ? Size : SpriteSize.Medium,
        VoiceEnabled,
        Placement,
        Enum.IsDefined(LastMode) ? LastMode : ChatMode.Workbench);
}
