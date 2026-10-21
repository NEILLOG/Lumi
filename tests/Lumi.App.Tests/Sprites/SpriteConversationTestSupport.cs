using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.App.Tests.Sprites;

/// <summary>小精靈對話整合測試共用的事件建構。</summary>
internal static class Ev
{
    public static ChatMessage Message(string text) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow);

    public static Completed Done(string text = "好", StopReason reason = StopReason.EndTurn) => new(Message(text), reason);

    public static Completed ToolTurnDone() => new(Message(""), StopReason.ToolUse);

    public static ToolCallStarted Tool(string callId = "c1", string name = "read_file", string display = "讀取檔案", string? target = "plan/00-master-plan.md",
        ToolPermission permission = ToolPermission.ReadOnly) =>
        new(callId, name, display, permission, target, "{}");

    public static ApprovalRequired Approval(string callId = "c1", string display = "寫入檔案", string? target = "notes/a.md",
        ToolPermission permission = ToolPermission.Write, string? detail = null) =>
        new(new ToolApprovalRequest(callId, "save_note", display, permission, new ToolPreview(target, detail), "{}"));

    public static ToolCallCompleted ToolDone(string callId = "c1", bool error = false) =>
        new(callId, new ToolResultBlock(callId, "ok", error, "讀取檔案", null, "完成", 10, ToolApproval.NotRequired));

    public static ChatRunResult Finished(ChatFailure? failure = null, bool cancelled = false, bool unexpected = false) =>
        new("conv", failure, unexpected, cancelled);

    public static ChatFailure Failure(ChatFailureKind kind = ChatFailureKind.Network, string message = "連不上伺服器", bool retryable = true) =>
        new(kind, message, retryable);
}

internal sealed class RecordingResponder : IToolApprovalResponder
{
    public List<(string CallId, ApprovalDecision Decision)> Calls { get; } = [];

    public void Resolve(string callId, ApprovalDecision decision) => Calls.Add((callId, decision));
}

internal sealed class FakeWindowHost : IWindowHost
{
    public bool IsWorkbenchVisible { get; private set; }
    public bool IsSpriteVisible { get; private set; }
    public List<string> Log { get; } = [];
    public bool Exited { get; private set; }
    public void ShowWorkbench() { IsWorkbenchVisible = true; Log.Add("showWorkbench"); }
    public void HideWorkbench() { IsWorkbenchVisible = false; Log.Add("hideWorkbench"); }
    public void ShowSprite() { IsSpriteVisible = true; Log.Add("showSprite"); }
    public void HideSprite() { IsSpriteVisible = false; Log.Add("hideSprite"); }
    public void ScrollWorkbenchToCall(string callId) => Log.Add($"scroll:{callId}");
    public void Exit() { Exited = true; Log.Add("exit"); }
}
