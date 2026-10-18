using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeClipboardService : IClipboardService
{
    public List<string> Texts { get; } = [];

    public Task SetTextAsync(string text)
    {
        Texts.Add(text);
        return Task.CompletedTask;
    }
}
