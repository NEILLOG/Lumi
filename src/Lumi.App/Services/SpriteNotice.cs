using Lumi.App.Sprites;
using Lumi.App.ViewModels;

namespace Lumi.App.Services;

/// <summary>預算提醒在小精靈模式的出口：交給小精靈氣泡說一句（8 秒後淡出，等待確認或錯誤時不蓋過）。</summary>
public sealed class SpriteNotice(SpriteBubbleViewModel bubble, IUiDispatcher dispatcher) : ISpriteNotice
{
    public void Say(string text) => dispatcher.Post(() => bubble.ShowNotice(text));
}
