using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeConfirmService : IConfirmService
{
    public bool Answer { get; set; } = true;

    public List<(string Message, string ConfirmText)> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string message, string confirmText)
    {
        Asked.Add((message, confirmText));
        return Task.FromResult(Answer);
    }
}
