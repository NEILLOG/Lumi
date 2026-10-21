namespace Lumi.Sprites.Abstractions;

/// <summary>殼呼叫 <see cref="ISpriteView.SetState"/> 時給的狀態；各狀態的意義與參考表現見 <c>docs/sprite-dev-guide.md</c> 第 2 節。</summary>
public enum SpriteState
{
    /// <summary>待機。每個角色都必須支援。</summary>
    Idle,
    Listening,
    Transcribing,
    Thinking,
    Tool,
    Streaming,
    Done,
    Attention,
    Error,
    Sleep,
}
