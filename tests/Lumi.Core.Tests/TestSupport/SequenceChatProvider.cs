using System.Runtime.CompilerServices;
using Lumi.Core.Chat;

namespace Lumi.Core.Tests.TestSupport;

/// <summary>每次呼叫回放下一組事件（工具迴圈會連續呼叫供應商多次）；腳本用完後再被呼叫會丟例外。</summary>
internal sealed class SequenceChatProvider : IChatProvider
{
    private readonly Queue<ChatEvent[]> _turns;

    public SequenceChatProvider(params ChatEvent[][] turns) => _turns = new Queue<ChatEvent[]>(turns);

    public List<ChatRequest> Requests { get; } = [];

    /// <summary>第幾次呼叫（從 1 起算）送完事件後停住等待取消；null 表示不停。</summary>
    public int? HangOnCall { get; init; }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Requests.Add(request);
        if (_turns.Count == 0)
            throw new InvalidOperationException("供應商腳本已用完，迴圈多呼叫了一次。");
        foreach (var chatEvent in _turns.Dequeue())
        {
            await Task.Yield();
            yield return chatEvent;
        }
        if (HangOnCall == Requests.Count)
            await Task.Delay(Timeout.Infinite, ct);
    }

    public static ChatEvent[] ToolCalls(params ToolUseBlock[] uses) =>
        [new Completed(ScriptedChatProvider.Assistant(uses), StopReason.ToolUse)];

    public static ChatEvent[] Answer(string text) =>
        [new TextDelta(text), new Completed(ScriptedChatProvider.Assistant(new TextBlock(text)), StopReason.EndTurn)];
}
