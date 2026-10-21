using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 自動休眠：待機且沒人靠近超過一段時間就進入 <see cref="SpriteState.Sleep"/>，游標靠近就喚醒。
/// 它只輸出「這一刻殼想要的狀態」，不碰角色。
/// </summary>
public sealed class SpriteSleepPolicy(SpriteShellOptions options, TimeProvider time)
{
    private DateTimeOffset _idleSince = time.GetUtcNow();

    public bool IsSleeping { get; private set; }

    /// <param name="requested">對話或示範流程想要的狀態。</param>
    /// <returns>實際要給角色的狀態（把休眠疊上去）。</returns>
    public SpriteState Update(SpriteState requested, bool near)
    {
        var now = time.GetUtcNow();

        if (requested == SpriteState.Sleep)
        {
            IsSleeping = true;   // 呼叫端明確要求休眠
            return SpriteState.Sleep;
        }

        if (requested != SpriteState.Idle || near)
        {
            // 有事做、或有人靠近：醒著，重新計時
            IsSleeping = false;
            _idleSince = now;
            return requested;
        }

        if (!IsSleeping && now - _idleSince > options.AutoSleep)
            IsSleeping = true;
        return IsSleeping ? SpriteState.Sleep : SpriteState.Idle;
    }

    /// <summary>使用者操作（點擊、輸入）時重新計時。</summary>
    public void Touch()
    {
        _idleSince = time.GetUtcNow();
        IsSleeping = false;
    }
}
