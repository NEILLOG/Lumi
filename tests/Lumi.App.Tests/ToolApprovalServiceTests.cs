using Lumi.App.Services;
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
}
