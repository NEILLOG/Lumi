using System.ComponentModel;
using System.Text.Json.Serialization;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.TestSupport;

/// <summary>可程式化的排程執行掛鉤：記錄暫停、恢復與加授權的順序。</summary>
public sealed class FakeScheduledRunHost : IScheduledRunHost
{
    /// <summary>對話 id 對應的進行中排程執行。</summary>
    public Dictionary<string, ActiveScheduledRun> Runs { get; } = [];

    /// <summary>依序記錄 <c>pause:工具名</c>、<c>resume</c>，以及 <c>inner</c>（測試替身記錄的呼叫順序）。</summary>
    public List<string> Events { get; } = [];

    public List<(string ScheduleId, ToolPermission Permission)> Grants { get; } = [];

    public ActiveScheduledRun? FindByConversation(string conversationId) =>
        Runs.TryGetValue(conversationId, out var run) ? run : null;

    public Task<IAsyncDisposable> PauseForApprovalAsync(string conversationId, ToolApprovalRequest request, CancellationToken ct)
    {
        Events.Add("pause:" + request.ToolName);
        return Task.FromResult<IAsyncDisposable>(new Resume(this));
    }

    public Task AddGrantAsync(string scheduleId, ToolPermission permission, CancellationToken ct)
    {
        Grants.Add((scheduleId, permission));
        return Task.CompletedTask;
    }

    private sealed class Resume(FakeScheduledRunHost host) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            host.Events.Add("resume");
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>內層的權限服務：依序回傳指定的回答（含修改後參數）；用完回 Deny。</summary>
public sealed class AnsweringApprovalService(params ApprovalAnswer[] answers) : IToolApprovalService
{
    private readonly Queue<ApprovalAnswer> _answers = new(answers);

    public List<ToolApprovalRequest> Requests { get; } = [];

    /// <summary>每次被詢問時呼叫（測試順序用）。</summary>
    public Action? OnRequest { get; set; }

    public async Task<ApprovalDecision> RequestAsync(ToolApprovalRequest request, CancellationToken ct) =>
        (await RequestAnswerAsync(request, ct)).Decision;

    public Task<ApprovalAnswer> RequestAnswerAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        OnRequest?.Invoke();
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : new ApprovalAnswer(ApprovalDecision.Deny));
    }
}

/// <summary>權限測試用：依建構時給的描述符，回應固定，記錄執行次數與參數。</summary>
public sealed class DescriptorTool(ToolDescriptor descriptor) : ToolBase<EchoArgs>
{
    public override ToolDescriptor Descriptor => descriptor;

    public List<EchoArgs> Args { get; } = [];

    protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct)
    {
        Args.Add(args);
        return Task.FromResult(ToolResult.Ok("完成"));
    }
}

/// <summary>模擬建立排程的工具：參數含 <c>granted_permissions</c>，<c>Describe</c> 回傳排程確認內容。</summary>
public sealed record ScheduleProbeArgs(
    [property: Description("要做的事")] string Text,
    [property: Description("授權勾選")][property: JsonPropertyName("granted_permissions")] string? GrantedPermissions = null);

public sealed class ScheduleProbeTool : ToolBase<ScheduleProbeArgs>
{
    public override ToolDescriptor Descriptor { get; } =
        new("create_probe", "建立排程", "測試用。", ToolPermission.Write, ToolModes.Both, TimeSpan.FromSeconds(5), AlwaysAsk: true);

    public List<ScheduleProbeArgs> Args { get; } = [];

    protected override Task<ToolResult> InvokeAsync(ScheduleProbeArgs args, ToolContext context, CancellationToken ct)
    {
        Args.Add(args);
        return Task.FromResult(ToolResult.Ok("完成"));
    }

    protected override ToolPreview Describe(ScheduleProbeArgs args, ToolContext context) =>
        new("排程", null, null, new ScheduleProposal("測試排程", "每天 9 點", null, args.Text, "測試模型", [], false, false));
}
