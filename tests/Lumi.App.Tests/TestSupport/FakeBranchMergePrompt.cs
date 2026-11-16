using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

/// <summary>帶回對話框的假實作（M36）：回 <see cref="Result"/>，並記下每次的請求。</summary>
internal sealed class FakeBranchMergePrompt : IBranchMergePrompt
{
    public BranchMergeResult Result { get; set; } = new(null);

    public List<BranchMergeRequest> Requests { get; } = [];

    public Task<BranchMergeResult> PromptAsync(BranchMergeRequest request)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}
