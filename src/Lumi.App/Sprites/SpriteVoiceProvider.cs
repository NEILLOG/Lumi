using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 目前角色的語氣（prompt 第 4 層，只在小精靈模式）：讀角色 manifest 的 <c>voice</c>，使用者可以在設定關閉。
/// 光球沒有語氣；素材型角色（M8）才會有，載入時拒絕指令式內容的檢查也在 M8，
/// 這裡只多一道保險：超過 <see cref="SpriteLimits.VoiceMaxLength"/> 字就不採用。
/// </summary>
public sealed class SpriteVoiceProvider(IModelCatalog settings, ISpriteCatalog sprites) : ISpriteVoiceProvider
{
    public string? GetVoice()
    {
        var sprite = settings.Current.Sprite;
        if (!sprite.VoiceEnabled)
            return null;

        var voice = (sprites.Find(sprite.SpriteId) ?? sprites.All.FirstOrDefault())?.Manifest.Voice?.Trim();
        return string.IsNullOrEmpty(voice) || voice.Length > SpriteLimits.VoiceMaxLength ? null : voice;
    }
}
