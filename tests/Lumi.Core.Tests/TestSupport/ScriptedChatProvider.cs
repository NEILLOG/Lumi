using System.Runtime.CompilerServices;
using Lumi.Core.Chat;

namespace Lumi.Core.Tests.TestSupport;

/// <summary>依序回放預先準備好的事件；<see cref="HangAfter"/> 之後會一直等到被取消。</summary>
internal sealed class ScriptedChatProvider : IChatProvider
{
    private readonly IReadOnlyList<ChatEvent> _events;

    public ScriptedChatProvider(params ChatEvent[] events) => _events = events;

    /// <summary>回放幾個事件之後就停住，等待取消；null 表示全部回放。</summary>
    public int? HangAfter { get; init; }

    public List<ChatRequest> Requests { get; } = [];

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Requests.Add(request);
        for (var i = 0; i < _events.Count; i++)
        {
            if (HangAfter == i)
                await Task.Delay(Timeout.Infinite, ct);
            await Task.Yield();
            yield return _events[i];
        }
        if (HangAfter == _events.Count)
            await Task.Delay(Timeout.Infinite, ct);
    }

    public static ChatMessage Assistant(params ContentBlock[] content) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, content, DateTimeOffset.UtcNow, ModelId: "test-model",
            Usage: new TokenUsage(10, 5, 0, 0));
}
