using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 工作台角色的對話接線（M27，plan/27 第 8.3 節）：<see cref="IChatSession"/> → <see cref="SpriteStateMapper"/>（自己的實例，不和小精靈視窗共用）
/// → <see cref="SpriteSleepPolicy"/>，並決定何時跳、何時探頭。不碰 Avalonia；角色與版面在 <c>WorkbenchSpritePresenter</c>。
/// </summary>
public sealed class WorkbenchSpriteController : IDisposable
{
    public WorkbenchSpriteController(IChatSession session, IToolApprovalResponder? approvals, SpriteShellOptions shell, TimeProvider time)
    {
        throw new NotImplementedException();   // TODO(27B)
    }

    public WorkbenchSpriteMotion Motion => throw new NotImplementedException();   // TODO(27B)

    /// <summary>角色現在該呈現的狀態（已疊上休眠，還沒依角色支援解析退化）。</summary>
    public SpriteState State => throw new NotImplementedException();   // TODO(27B)

    /// <summary><see cref="State"/> 改變。</summary>
    public event Action<SpriteState>? StateChanged
    {
        add => throw new NotImplementedException();   // TODO(27B)
        remove => throw new NotImplementedException();   // TODO(27B)
    }

    /// <summary>目前版位。只有 <see cref="WorkbenchSpriteSlot.Toolbar"/> 才會跳與探頭。</summary>
    public WorkbenchSpriteSlot Slot
    {
        get => throw new NotImplementedException();   // TODO(27B)
        set => throw new NotImplementedException();   // TODO(27B)
    }

    /// <summary>開始接收對話事件。已經在回應中就直接進思考。重複呼叫沒有作用。</summary>
    public void Attach() => throw new NotImplementedException();   // TODO(27B)

    /// <summary>停止接收對話事件（功能關閉）。</summary>
    public void Detach() => throw new NotImplementedException();   // TODO(27B)

    /// <summary>游標靠近或離開（由殼的 <see cref="SpriteInteractionTracker"/> 提供）。</summary>
    public void SetNear(bool near) => throw new NotImplementedException();   // TODO(27B)

    /// <summary>使用者碰了角色：重新計時休眠。</summary>
    public void Touch() => throw new NotImplementedException();   // TODO(27B)

    /// <summary>每個輪詢週期呼叫：完成狀態回待機、休眠、探頭。</summary>
    public void Tick() => throw new NotImplementedException();   // TODO(27B)

    public void Dispose() => Detach();
}
