using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 目前角色的語氣（prompt 第 4 層，只在小精靈模式）：讀角色 manifest 的 <c>voice</c>，使用者可以在設定關閉。
/// 光球沒有語氣；素材型角色（M8）才會有。載入時已經檢查過，這裡再用同一個 <see cref="SpriteVoiceValidator"/> 檢查一次
/// （程式型角色不經過載入器，所以也要擋）：沒通過就當作沒有語氣。
/// </summary>
public sealed class SpriteVoiceProvider(IModelCatalog settings, ISpriteCatalog sprites) : ISpriteVoiceProvider
{
    public string? GetVoice()
    {
        var sprite = settings.Current.Sprite;
        if (!sprite.VoiceEnabled)
            return null;

        return SpriteVoiceValidator.Check((sprites.Find(sprite.SpriteId) ?? sprites.All.FirstOrDefault())?.Manifest.Voice).Voice;
    }
}
