using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.Core.Tools;

namespace Lumi.App.Tests;

public class ToolApprovalServiceTests
{
    private static ToolApprovalRequest Request(string id = "c1") =>
        new(id, "save", "儲存", ToolPermission.Write, ToolPreview.None, "{}");

    [Fact]
    public async Task ResolveAfterRequest_CompletesTheWait()
    {
        var service = new ToolApprovalService();
        var wait = service.RequestAsync(Request(), CancellationToken.None);
        Assert.False(wait.IsCompleted);

        service.Resolve("c1", ApprovalDecision.AllowForConversation);

        Assert.Equal(ApprovalDecision.AllowForConversation, await wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ResolveBeforeRequest_IsNotLost()
    {
        var service = new ToolApprovalService();
        service.Resolve("c1", ApprovalDecision.Deny);

        var decision = await service.RequestAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ApprovalDecision.Deny, decision);
    }

    [Fact]
    public async Task Cancellation_EndsTheWait_WithOperationCanceled()
    {
        var service = new ToolApprovalService();
        using var cts = new CancellationTokenSource();
        var wait = service.RequestAsync(Request(), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task DifferentCalls_AreIndependent()
    {
        var service = new ToolApprovalService();
        var first = service.RequestAsync(Request("a"), CancellationToken.None);
        var second = service.RequestAsync(Request("b"), CancellationToken.None);

        service.Resolve("b", ApprovalDecision.AllowOnce);

        Assert.Equal(ApprovalDecision.AllowOnce, await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(first.IsCompleted);
        service.Resolve("a", ApprovalDecision.Deny);
        Assert.Equal(ApprovalDecision.Deny, await first.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SameCallIdCanBeRequestedAgainAfterItFinished()
    {
        var service = new ToolApprovalService();
        var first = service.RequestAsync(Request(), CancellationToken.None);
        service.Resolve("c1", ApprovalDecision.AllowOnce);
        await first;

        var second = service.RequestAsync(Request(), CancellationToken.None);

        Assert.False(second.IsCompleted);   // 上一次的決定不會殘留到這一次
        service.Resolve("c1", ApprovalDecision.Deny);
        Assert.Equal(ApprovalDecision.Deny, await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RequestAnswerAsync_ReceivesTheAmendedArguments()
    {
        var service = new ToolApprovalService();
        var wait = service.RequestAnswerAsync(Request(), CancellationToken.None);

        service.Resolve("c1", new ApprovalAnswer(ApprovalDecision.AllowOnce, """{"granted_permissions":[]}"""));

        var answer = await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ApprovalDecision.AllowOnce, answer.Decision);
        Assert.Equal("""{"granted_permissions":[]}""", answer.AmendedArgumentsJson);
    }

    [Fact]
    public async Task AnswerBeforeRequest_KeepsTheAmendedArguments()
    {
        var service = new ToolApprovalService();
        service.Resolve("c1", new ApprovalAnswer(ApprovalDecision.AllowOnce, "{}"));

        var answer = await service.RequestAnswerAsync(Request(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("{}", answer.AmendedArgumentsJson);
    }

    [Fact]
    public async Task RequestAsync_StillReturnsTheDecision_AndPlainResolveHasNoAmendment()
    {
        var service = new ToolApprovalService();
        var wait = service.RequestAsync(Request(), CancellationToken.None);

        service.Resolve("c1", ApprovalDecision.AllowForConversation);

        Assert.Equal(ApprovalDecision.AllowForConversation, await wait.WaitAsync(TimeSpan.FromSeconds(2)));
        var second = service.RequestAnswerAsync(Request(), CancellationToken.None);
        service.Resolve("c1", ApprovalDecision.Deny);
        Assert.Null((await second.WaitAsync(TimeSpan.FromSeconds(2))).AmendedArgumentsJson);
    }

    [Fact]
    public async Task Cancellation_EndsTheAnswerWait()
    {
        var service = new ToolApprovalService();
        using var cts = new CancellationTokenSource();
        var wait = service.RequestAnswerAsync(Request(), cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Resolved_FiresWithTheDecision_ForAnswersWithArguments()
    {
        var service = new ToolApprovalService();
        ApprovalResolved? seen = null;
        service.Resolved += (_, e) => seen = e;

        service.Resolve("c1", new ApprovalAnswer(ApprovalDecision.AllowOnce, "{}"));

        Assert.Equal(new ApprovalResolved("c1", ApprovalDecision.AllowOnce), seen);
    }
}
