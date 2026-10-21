using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeFolderOpener(bool succeeds = true) : IFolderOpener
{
    public List<string> Opened { get; } = [];

    public Task<bool> OpenAsync(string directory)
    {
        Opened.Add(directory);
        return Task.FromResult(succeeds);
    }
}
