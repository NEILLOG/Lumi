namespace Lumi.App.Sprites;

/// <summary>
/// 工作台角色的程式動作（M27，plan/27 第 8.3 節）：跳一下、瞬移、探頭。純邏輯，吃時間、吐偏移與透明度，不碰畫面。
/// 呼叫端每一幀呼叫 <see cref="Update"/>，再讀 <see cref="OffsetY"/>、<see cref="Opacity"/>。
/// </summary>
public sealed class WorkbenchSpriteMotion
{
    public WorkbenchSpriteMotion(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);   // TODO(27B)
    }

    /// <summary>垂直偏移（整數 DIP，往下為正）：探頭下沉量加跳躍高度。</summary>
    public double OffsetY => throw new NotImplementedException();   // TODO(27B)

    public double Opacity => throw new NotImplementedException();   // TODO(27B)

    /// <summary>有動作在進行：呼叫端要維持高幀率。</summary>
    public bool IsAnimating => throw new NotImplementedException();   // TODO(27B)

    /// <summary>瞬移進行中。</summary>
    public bool IsTeleporting => throw new NotImplementedException();   // TODO(27B)

    /// <summary>最後一次開始瞬移的時間；還沒瞬移過是 null。</summary>
    public DateTimeOffset? LastTeleportAt => throw new NotImplementedException();   // TODO(27B)

    /// <summary>探頭的完整下沉量。0＝這個位置沒有探頭（歡迎區）：立刻回到全身。</summary>
    public void SetPeekDepth(double depth) => throw new NotImplementedException();   // TODO(27B)

    /// <summary>冒出來（線性，完整下沉量 200 ms）；還在沉就從目前位置往回。</summary>
    public void Rise() => throw new NotImplementedException();   // TODO(27B)

    /// <summary>縮回去（線性，完整下沉量 300 ms）。</summary>
    public void Sink() => throw new NotImplementedException();   // TODO(27B)

    /// <summary>跳一下。跳躍中、瞬移中不理會；還在冒出來就等冒完才跳。</summary>
    public void Jump() => throw new NotImplementedException();   // TODO(27B)

    /// <summary>瞬移：淡出 120 ms 後呼叫 <paramref name="swap"/>（呼叫端在這裡換位置），再淡入到 300 ms。進行中再呼叫不理會。</summary>
    public void BeginTeleport(Action swap) => throw new NotImplementedException();   // TODO(27B)

    /// <summary>推進到現在的時間。</summary>
    public void Update() => throw new NotImplementedException();   // TODO(27B)

    /// <summary>取消所有進行中的動作，回到全身、不透明（視窗隱藏、關閉功能時）。還沒換位置的瞬移，<c>swap</c> 會立刻被呼叫。</summary>
    public void Reset() => throw new NotImplementedException();   // TODO(27B)
}
