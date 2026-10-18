using System.ComponentModel;
using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.TestSupport;

public sealed record EchoArgs(
    [property: Description("要回聲的文字")] string Text,
    [property: Description("重複幾次")] int Times = 1);

public sealed record NoArgs;

/// <summary>可程式化的假工具：測試用來控制結果、延遲、例外與記錄呼叫。</summary>
public sealed class FakeTool : ToolBase<EchoArgs>
{
    private readonly ToolDescriptor _descriptor;

    public FakeTool(string name = "echo", ToolPermission permission = ToolPermission.ReadOnly, ToolModes modes = ToolModes.Both,
        TimeSpan? timeout = null, string description = "把文字回聲回來。測試用。")
    {
        _descriptor = new ToolDescriptor(name, "回聲", description, permission, modes, timeout ?? TimeSpan.FromSeconds(5));
    }

    public override ToolDescriptor Descriptor => _descriptor;

    public Func<EchoArgs, CancellationToken, Task<ToolResult>>? Behavior { get; set; }

    public List<(EchoArgs Args, ToolContext Context)> Calls { get; } = [];

    protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct)
    {
        Calls.Add((args, context));
        return Behavior is not null
            ? Behavior(args, ct)
            : Task.FromResult(ToolResult.Ok(string.Concat(Enumerable.Repeat(args.Text, args.Times)), $"回聲 {args.Times} 次"));
    }

    protected override ToolPreview Describe(EchoArgs args) => new(args.Text, $"將回聲 {args.Times} 次");
}

public sealed class ScriptedApprovalService : IToolApprovalService
{
    private readonly Queue<ApprovalDecision> _decisions;

    public ScriptedApprovalService(params ApprovalDecision[] decisions) => _decisions = new Queue<ApprovalDecision>(decisions);

    public List<ToolApprovalRequest> Requests { get; } = [];

    /// <summary>不為 null 時，詢問會一直等到被取消或這個工作完成（測試「等待確認中按停止」）。</summary>
    public TaskCompletionSource<ApprovalDecision>? Gate { get; set; }

    public async Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Gate is { } gate)
            return await gate.Task.WaitAsync(ct);
        return _decisions.Count > 0 ? _decisions.Dequeue() : ApprovalDecision.Deny;
    }
}
