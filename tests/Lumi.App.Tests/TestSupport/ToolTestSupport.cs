using System.Runtime.CompilerServices;
using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.App.Tests.TestSupport;

public sealed record EchoArgs(string Text);

/// <summary>可設定名稱、權限與行為的假工具。</summary>
public sealed class LambdaTool : ToolBase<EchoArgs>
{
    private readonly ToolDescriptor _descriptor;

    public LambdaTool(string name, string displayName, ToolPermission permission = ToolPermission.ReadOnly)
    {
        _descriptor = new ToolDescriptor(name, displayName, "測試用工具。", permission, ToolModes.Both, TimeSpan.FromSeconds(5));
    }

    public override ToolDescriptor Descriptor => _descriptor;

    public Func<EchoArgs, CancellationToken, Task<ToolResult>>? Behavior { get; set; }

    public int Runs { get; private set; }

    protected override ToolPreview Describe(EchoArgs args, ToolContext context) => new(args.Text, $"會處理「{args.Text}」");

    protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct)
    {
        Runs++;
        return Behavior is { } behavior
            ? behavior(args, ct)
            : Task.FromResult(ToolResult.Ok($"結果：{args.Text}", $"完成 {args.Text}"));
    }
}

/// <summary>每次呼叫回放下一組事件；<see cref="HangOnCall"/> 指定的那次呼叫（從 1 起算）送完事件後停住等待取消。</summary>
internal sealed class SequenceChatProvider : IChatProvider
{
    private readonly Queue<ChatEvent[]> _turns;

    public SequenceChatProvider(params ChatEvent[][] turns) => _turns = new Queue<ChatEvent[]>(turns);

    public int? HangOnCall { get; init; }

    public int Calls { get; private set; }

    public List<ChatRequest> Requests { get; } = [];

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls++;
        Requests.Add(request);
        foreach (var chatEvent in _turns.Dequeue())
        {
            await Task.Yield();
            yield return chatEvent;
        }
        if (HangOnCall == Calls)
            await Task.Delay(Timeout.Infinite, ct);
    }

    public static ChatEvent[] ToolCalls(string? text, params ToolUseBlock[] uses)
    {
        List<ContentBlock> content = [];
        if (text is not null)
            content.Add(new TextBlock(text));
        content.AddRange(uses);
        return
        [
            .. text is null ? [] : new ChatEvent[] { new TextDelta(text) },
            new Completed(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, content, DateTimeOffset.UtcNow,
                ModelId: "m", Usage: new TokenUsage(10, 5, 0, 0)), StopReason.ToolUse),
        ];
    }

    public static ChatEvent[] Answer(string text) =>
    [
        new TextDelta(text),
        new Completed(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow,
            ModelId: "m", Usage: new TokenUsage(20, 8, 0, 0)), StopReason.EndTurn),
    ];

    public static ToolUseBlock Use(string id, string name, string text = "甲") => new(id, name, $$"""{"text":"{{text}}"}""");
}
