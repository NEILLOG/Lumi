namespace Lumi.Sprites.Abstractions;

/// <summary>
/// 把殼想要的狀態對應到角色真的有表現的狀態。退化規則可以串接
/// （<c>Transcribing → Listening → Idle</c>），一路往下找，最後一定落到 <see cref="SpriteState.Idle"/>。
/// <b>由殼負責解析</b>，角色的 <see cref="ISpriteView.SetState"/> 只會收到它自己支援的狀態。
/// </summary>
public static class SpriteStateResolver
{
    /// <summary>預設的退化規則（<c>docs/sprite-dev-guide.md</c> 第 2 節）。</summary>
    public static IReadOnlyDictionary<SpriteState, SpriteState> DefaultFallback { get; } = new Dictionary<SpriteState, SpriteState>
    {
        [SpriteState.Listening] = SpriteState.Idle,
        [SpriteState.Transcribing] = SpriteState.Listening,
        [SpriteState.Thinking] = SpriteState.Idle,
        [SpriteState.Tool] = SpriteState.Thinking,
        [SpriteState.Streaming] = SpriteState.Idle,
        [SpriteState.Done] = SpriteState.Idle,
        [SpriteState.Attention] = SpriteState.Done,
        [SpriteState.Error] = SpriteState.Idle,
        [SpriteState.Sleep] = SpriteState.Idle,
    };

    public static SpriteState Resolve(SpriteState state, SpriteManifest manifest)
    {
        var visited = new HashSet<SpriteState>();
        var current = state;
        while (current != SpriteState.Idle && !manifest.Supported.Contains(current))
        {
            if (!visited.Add(current))
                return SpriteState.Idle;   // 退化規則有循環

            if (manifest.Fallback.TryGetValue(current, out var next) || DefaultFallback.TryGetValue(current, out next))
                current = next;
            else
                return SpriteState.Idle;
        }
        return current;
    }
}
