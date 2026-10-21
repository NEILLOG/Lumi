using Avalonia;
using Avalonia.Controls;

namespace Lumi.Sprites.Abstractions;

/// <summary>
/// 一個畫出來的角色。所有座標的原點都是 <see cref="Visual"/> 的左上角。
/// 角色不可以開視窗、碰平台 API、讀對話內容或自己開計時器（<c>docs/sprite-dev-guide.md</c> 第 1 節）。
/// </summary>
public interface ISpriteView
{
    /// <summary>角色的畫面，大小是 <see cref="SpriteOptions.Outer"/>。</summary>
    Control Visual { get; }

    /// <summary>殼已經依退化規則解析過：只會收到 <see cref="SpriteManifest.Supported"/> 裡的狀態。</summary>
    void SetState(SpriteState state);

    void OnInteraction(SpriteInteraction e);

    /// <summary>由殼的 FrameDriver 統一呼叫；角色在這裡推進時間並要求重繪。</summary>
    void Advance(TimeSpan dt);

    /// <summary>角色本體的範圍，用於點擊穿透與戳。角色框外一律回傳 false。</summary>
    bool HitTest(Point p);

    /// <summary>氣泡接在哪裡（通常是頭頂）。</summary>
    Point BubbleAnchor { get; }

    /// <summary>角色目前需要的幀率；殼會取它與殼自己需求中較大者。</summary>
    int PreferredFps { get; }
}
