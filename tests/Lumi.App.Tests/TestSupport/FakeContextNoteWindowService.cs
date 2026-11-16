using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

/// <summary>研究本視窗服務的假實作（M35）：記下呼叫；<see cref="ShowAsync"/> 後 <see cref="IsOpen"/> 是 true。</summary>
internal sealed class FakeContextNoteWindowService : IContextNoteWindowService
{
    public bool IsOpen { get; set; }

    /// <summary>格式「Show:對話 id」「Follow:對話 id 或 null」「Close」。</summary>
    public List<string> Calls { get; } = [];

    public Task ShowAsync(string conversationId)
    {
        Calls.Add($"Show:{conversationId}");
        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task FollowConversationAsync(string? conversationId)
    {
        Calls.Add($"Follow:{conversationId ?? "null"}");
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        Calls.Add("Close");
        IsOpen = false;
        return Task.CompletedTask;
    }
}
