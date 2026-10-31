using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

/// <summary><see cref="IScheduledRunViewer"/> 的假實作（M24）：記下打開的 RunId。</summary>
internal sealed class FakeScheduledRunViewer : IScheduledRunViewer
{
    public List<string> Opened { get; } = [];

    public Task OpenScheduledRunAsync(string runId)
    {
        Opened.Add(runId);
        return Task.CompletedTask;
    }
}
