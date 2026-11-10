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

    /// <summary>每次 <see cref="SetMarkdownAsync"/> 收到的 Markdown（也記進 <see cref="Texts"/>：純文字那份就是原文）。</summary>
    public List<string> Markdowns { get; } = [];

    public Task SetMarkdownAsync(string markdown)
    {
        Markdowns.Add(markdown);
        Texts.Add(markdown);
        return Task.CompletedTask;
    }
}
